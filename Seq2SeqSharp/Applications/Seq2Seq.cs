﻿// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/Seq2SeqSharp
//
// This file is part of Seq2SeqSharp.
//
// Seq2SeqSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// Seq2SeqSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System;
using System.Collections.Generic;
using System.IO;
using AdvUtils;
using Microsoft.Extensions.Caching.Memory;
using Seq2SeqSharp.Enums;
using Seq2SeqSharp.Corpus;
using Seq2SeqSharp.Layers;
using Seq2SeqSharp.Models;
using Seq2SeqSharp.Tools;
using Seq2SeqSharp.Utils;
using TensorSharp;

namespace Seq2SeqSharp.Applications
{
    public class Seq2Seq : BaseSeq2SeqFramework<Seq2SeqModel>
    {
        // Trainable parameters including networks and tensors
        private MultiProcessorNetworkWrapper<IWeightTensor> m_srcEmbedding; //The embeddings over devices for target
        private MultiProcessorNetworkWrapper<IWeightTensor> m_tgtEmbedding; //The embeddings over devices for source

        private MultiProcessorNetworkWrapper<IEncoder> m_encoder; //The encoders over devices.
        private MultiProcessorNetworkWrapper<IDecoder> m_decoder; //The decoders over devices
        private MultiProcessorNetworkWrapper<IFeedForwardLayer> m_decoderFFLayer; //The feed forward layers over devices after all layers in decoder

        private MultiProcessorNetworkWrapper<IWeightTensor> m_posEmbedding = null;
        private MultiProcessorNetworkWrapper<IWeightTensor> m_segmentEmbedding;

        private MultiProcessorNetworkWrapper<IFeedForwardLayer> m_pointerGenerator;

        private readonly PaddingEnums m_paddingType = PaddingEnums.AllowPadding;
        readonly Seq2SeqOptions m_options = null;

        private MemoryCache m_memoryCache;

        public Seq2Seq(Seq2SeqOptions options, Vocab srcVocab = null, Vocab tgtVocab = null)
            : base(deviceIds: options.DeviceIds, processorType: options.ProcessorType, modelFilePath: options.ModelFilePath, memoryUsageRatio: options.MemoryUsageRatio, 
                  compilerOptions: options.CompilerOptions, runValidEveryUpdates: options.RunValidEveryUpdates, updateFreq: options.UpdateFreq, 
                  startToRunValidAfterUpdates: options.StartValidAfterUpdates, maxDegressOfParallelism: options.TaskParallelism, mklInstructions: options.MKLInstructions, 
                  weightsUpdateCount: options.WeightsUpdateCount, cudaMemoryAllocatorType: options.CudaMemoryAllocatorType, elementType: options.AMP ? DType.Float16 : DType.Float32, saveModelEveryUpdats: options.SaveModelEveryUpdates, saveGPUMemoryMode: options.SaveGPUMemoryMode)
        {
            m_paddingType = options.PaddingType;
            m_options = options;

            // Check if options are valided.
            m_options.ValidateOptions();

            m_memoryCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = 1024
            });

            if (File.Exists(m_options.ModelFilePath))
            {
                if (srcVocab != null || tgtVocab != null)
                {
                    throw new ArgumentException($"Model '{m_options.ModelFilePath}' exists and it includes vocabulary, so input vocabulary must be null.");
                }

                // Model file exists, so we load it from file.
                m_modelMetaData = LoadModel();
            }
            else
            {
                // Model doesn't exist, we create it and initlaize parameters
                m_modelMetaData = new Seq2SeqModel(options, srcVocab, tgtVocab);

                //Initializng weights in encoders and decoders
                CreateTrainableParameters(m_modelMetaData);
            }

            m_modelMetaData.ShowModelInfo();
        }

        public void UpdateVocabs(Vocab srcVocab, Vocab tgtVocab)
        {
            if (srcVocab != null)
            {
                m_modelMetaData.SrcVocab = srcVocab;
            }

            if (tgtVocab != null)
            {
                m_modelMetaData.TgtVocab = tgtVocab;
            }

            SaveModel(createBackupPrevious: true);
        }


        protected override Seq2SeqModel LoadModel(string suffix = "") => base.LoadModelRoutine<Model_4_ProtoBufSerializer>(CreateTrainableParameters, Seq2SeqModel.Create, suffix);

        private bool CreateTrainableParameters(IModel model)
        {
            Logger.WriteLine(Logger.Level.debug, $"Creating encoders and decoders...");

            var raDeviceIds = new RoundArray<int>(DeviceIds);
            DType elementType = m_options.AMP ? DType.Float16 : DType.Float32;

            m_encoder = Encoder.CreateEncoders(model, m_options, raDeviceIds, elementType: elementType);
            m_decoder = Decoder.CreateDecoders(model, m_options, raDeviceIds, elementType: elementType);
            m_decoderFFLayer = new MultiProcessorNetworkWrapper<IFeedForwardLayer>(new FeedForwardLayer("FeedForward_Decoder_0", model.HiddenDim, model.TgtVocab.Count, dropoutRatio: 0.0f, deviceId: raDeviceIds.GetNextItem(),
                isTrainable: true, learningRateFactor: m_options.DecoderStartLearningRateFactor, elementType: elementType), DeviceIds);
            (m_posEmbedding, m_segmentEmbedding) = Misc.CreateAuxEmbeddings(raDeviceIds, model.HiddenDim, Math.Max(Math.Max(m_options.MaxSrcSentLength, m_options.MaxValidSrcSentLength), Math.Max(m_options.MaxTgtSentLength, m_options.MaxValidTgtSentLength)), model, 
                elementType: elementType, createAPE: (model.PEType == PositionEmbeddingEnums.APE));
            (m_srcEmbedding, m_tgtEmbedding) = CreateSrcTgtEmbeddings(model, raDeviceIds, m_options.IsSrcEmbeddingTrainable, m_options.IsTgtEmbeddingTrainable, m_options.EncoderStartLearningRateFactor, m_options.DecoderStartLearningRateFactor, elementType: elementType);


            if (model.PointerGenerator)
            {
                if (model.SharedEmbeddings == false)
                {
                    throw new ArgumentException($"Shared embeddings is required to true for pointer generator.");
                }

                Logger.WriteLine(Logger.Level.debug, $"Create pointer generator weights...");

                m_pointerGenerator = new MultiProcessorNetworkWrapper<IFeedForwardLayer>(new FeedForwardLayer("PointerGenerator_0", model.HiddenDim, 1, dropoutRatio: 0.0f, deviceId: raDeviceIds.GetNextItem(),
                isTrainable: true, learningRateFactor: m_options.DecoderStartLearningRateFactor, elementType: TensorSharp.DType.Float32), DeviceIds); // We always use Float32 type for pointer generator even AMP = true
            }
            else
            {
                m_pointerGenerator = null;
            }

            return (true);
        }

        public void VQModel()
        {
            m_modelMetaData.VQType = m_options.VQType;
            SaveModel(createBackupPrevious: true, suffix: ".vq");
        }

        /// <summary>
        /// Get networks on specific devices
        /// </summary>
        private (IEncoder, IDecoder, IFeedForwardLayer, IWeightTensor, IWeightTensor, IWeightTensor, IFeedForwardLayer, IWeightTensor) GetNetworksOnDeviceAt(int deviceId)
        {
            var deviceIdIdx = TensorAllocator.GetDeviceIdIndex(deviceId);
            return (m_encoder.GetNetworkOnDevice(deviceIdIdx),
                    m_decoder.GetNetworkOnDevice(deviceIdIdx),
                    m_decoderFFLayer.GetNetworkOnDevice(deviceIdIdx),
                    m_srcEmbedding.GetNetworkOnDevice(deviceIdIdx),
                    m_modelMetaData.SharedEmbeddings ? m_srcEmbedding.GetNetworkOnDevice(deviceIdIdx) : m_tgtEmbedding.GetNetworkOnDevice(deviceIdIdx),
                    m_segmentEmbedding?.GetNetworkOnDevice(deviceIdIdx), m_pointerGenerator?.GetNetworkOnDevice(deviceIdIdx), m_posEmbedding?.GetNetworkOnDevice(deviceIdIdx));
        }

        private string GenerateCacheKey(List<List<string>> strs)
        {
            List<string> r = new List<string>();

            foreach (var str in strs)
            {
                r.Add(string.Join(" ", str));
            }

            return string.Join("\t", r);
        }


        /// <summary>
        /// Run forward part on given single device
        /// </summary>
        /// <param name="computeGraph">The computing graph for current device. It gets created and passed by the framework</param>
        /// <param name="srcSnts">A batch of input tokenized sentences in source side</param>
        /// <param name="tgtSnts">A batch of output tokenized sentences in target side</param>
        /// <param name="deviceIdIdx">The index of current device</param>
        /// <returns>The cost of forward part</returns>
        public override List<NetworkResult> RunForwardOnSingleDevice(IComputeGraph computeGraph, IPairBatch sntPairBatch, DecodingOptions decodingOptions, bool isTraining)
        {
            (var encoder, var decoder, var decoderFFLayer, var srcEmbedding, var tgtEmbedding, var segmentEmbedding, var pointerGenerator, var posEmbeddings) = GetNetworksOnDeviceAt(computeGraph.DeviceId);

            var srcSnts = sntPairBatch.GetSrcTokens();
            var originalSrcLengths = BuildInTokens.PadSentences(srcSnts);
            var srcTokensList = m_modelMetaData.SrcVocab.GetWordIndex(srcSnts);

            //if (isTraining && srcSnts[0].Count > m_options.MaxSrcSentLength + 2)
            //{
            //    throw new InvalidDataException($"The source sentence is too long. Its length = '{srcSnts[0].Count}', but MaxSrcSentLength is '{m_options.MaxSrcSentLength}'. The sentence is '{string.Join(" ", srcSnts[0])}'");
            //}

            IWeightTensor encOutput;
            if (!isTraining && (m_options.ProcessorType == ProcessorTypeEnums.CPU))
            {
                // Try to get src tensor from cache
                string cacheKey = GenerateCacheKey(srcSnts);
                if (!m_memoryCache.TryGetValue(cacheKey, out encOutput))
                {
                    encOutput = Encoder.Run(computeGraph, encoder, m_modelMetaData, m_paddingType, srcEmbedding, posEmbeddings, segmentEmbedding, srcTokensList, originalSrcLengths); // Shape: [batchsize * seqLen, embedding_dim]

                    var cacheEntryOptions = new MemoryCacheEntryOptions().SetSize(1);
                    m_memoryCache.Set(cacheKey, encOutput.CopyWeightsRef($"cache_{encOutput.Name}", false, graphToBind: null), cacheEntryOptions);
                }
            }
            else
            {
                // Compute src tensor
                encOutput = Encoder.Run(computeGraph, encoder, m_modelMetaData, m_paddingType, srcEmbedding, posEmbeddings, segmentEmbedding, srcTokensList, originalSrcLengths, amp:m_options.AMP);
            }

            List<NetworkResult> nrs = new List<NetworkResult>();

            // Generate output decoder sentences
            int batchSize = srcSnts.Count;
            var tgtSnts = sntPairBatch.GetTgtTokens();
            var tgtTokensList = m_modelMetaData.TgtVocab.GetWordIndex(tgtSnts);
            NetworkResult nr = new NetworkResult();
            nr.Status = NetworkResultStatus.SUCCEED;

            decoder.Reset(computeGraph.GetWeightFactory(), srcSnts.Count);

            if (decoder is AttentionDecoder)
            {
                nr.Cost = Decoder.DecodeAttentionLSTM(tgtTokensList, computeGraph, encOutput, decoder as AttentionDecoder, decoderFFLayer, tgtEmbedding, m_modelMetaData.TgtVocab, srcSnts.Count, isTraining);
                nr.Output = new List<List<List<string>>>
                {
                    m_modelMetaData.TgtVocab.ConvertIdsToString(tgtTokensList)
                };
            }
            else
            {
                if (isTraining)
                {
                    (var c, _) = Decoder.DecodeTransformer(tgtTokensList, computeGraph, encOutput, decoder as TransformerDecoder, decoderFFLayer, tgtEmbedding, originalSrcLengths, m_modelMetaData.TgtVocab, m_paddingType,
                        m_options.DropoutRatio, decodingOptions, isTraining, pointerGenerator: pointerGenerator, srcSeqs: srcTokensList, lossType: m_options.LossType, focalLossGamma: m_options.FocalLossGamma, 
                        segmentEmbeddings: segmentEmbedding, amp: m_options.AMP, posEmbeddings: posEmbeddings);
                    nr.Cost = c;
                    nr.Output = null;
                }
                else if (m_options.Task == ModeEnums.Alignment)
                {
                    if (decodingOptions.OutputAligmentsToSrc)
                    {
                        if (pointerGenerator == null)
                        {
                            throw new ArgumentException($"Only pointer generator model can output alignments to source sequence.");
                        }
                    }

                    using var g = computeGraph.CreateSubGraph($"TransformerDecoder_Alignment");
                    (var cost2, var bssSeqList) = Decoder.DecodeTransformer(tgtTokensList, g, encOutput, decoder as TransformerDecoder, decoderFFLayer, tgtEmbedding,
                                                                               originalSrcLengths, m_modelMetaData.TgtVocab, m_paddingType, 0.0f, decodingOptions, isTraining,
                                                                               outputSentScore: decodingOptions.BeamSearchSize > 1, pointerGenerator: pointerGenerator, 
                                                                               srcSeqs: srcTokensList, teacherForcedAlignment: true, lossType: m_options.LossType, segmentEmbeddings: segmentEmbedding, amp: m_options.AMP, posEmbeddings: posEmbeddings);
                    nr.Cost = 0.0f;
                    nr.Output = m_modelMetaData.TgtVocab.CovertToWords(bssSeqList);
                    if (decodingOptions.OutputAligmentsToSrc)
                    {
                        (nr.Alignments, nr.AlignmentScores) = Decoder.ExtractAlignments(bssSeqList);
                    }
                }
                else
                {   // Test mode or running validation in Training mode
                    Dictionary<string, IWeightTensor> cachedTensors = new Dictionary<string, IWeightTensor>();
                    List<List<BeamSearchStatus>> beam2batchStatus = Decoder.InitBeamSearchStatusListList(batchSize, tgtTokensList);
                    for (int i = tgtTokensList[0].Count; i < decodingOptions.MaxTgtSentLength; i++)
                    {
                        List<List<BeamSearchStatus>> batch2beam2seq = null; //(batch_size, beam_search_size)
                        try
                        {
                            foreach (var batchStatus in beam2batchStatus)
                            {
                                var batch2tgtTokens = Decoder.ExtractBatchTokens(batchStatus);
                                List<List<int>> alignmentsToSrc = null;
                                List<List<float>> alignmentScores = null;
                                if (decodingOptions.OutputAligmentsToSrc)
                                {
                                    if (pointerGenerator == null)
                                    {
                                        throw new ArgumentException($"Only pointer generator model can output alignments to source sequence.");
                                    }

                                    (alignmentsToSrc, alignmentScores) = Decoder.ExtractBatchAlignments(batchStatus);
                                }

                                using var g = computeGraph.CreateSubGraph($"TransformerDecoder_Step_{i}");
                                (var cost2, var bssSeqList) = Decoder.DecodeTransformer(batch2tgtTokens, g, encOutput, decoder as TransformerDecoder, decoderFFLayer, tgtEmbedding,
                                                                                originalSrcLengths, m_modelMetaData.TgtVocab, m_paddingType, 0.0f, decodingOptions, isTraining,
                                                                                outputSentScore: decodingOptions.BeamSearchSize > 1, previousBeamSearchResults: batchStatus,
                                                                                pointerGenerator: pointerGenerator, srcSeqs: srcTokensList,
                                                                                cachedTensors: cachedTensors, alignmentsToSrc: alignmentsToSrc, alignmentScoresToSrc: alignmentScores, 
                                                                                blockedTokens: decodingOptions.BlockedTokens, segmentEmbeddings: segmentEmbedding, amp: m_options.AMP, posEmbeddings: posEmbeddings);

                                bssSeqList = Decoder.SwapBeamAndBatch(bssSeqList); // Swap shape: (beam_search_size, batch_size) -> (batch_size, beam_search_size)
                                batch2beam2seq = Decoder.CombineBeamSearchResults(batch2beam2seq, bssSeqList);
                            }
                        }
                        catch (OutOfMemoryException)
                        {
                            GC.Collect();
                            Logger.WriteLine(Logger.Level.warn, $"We have out of memory while generating '{i}th' tokens, so terminate decoding for current sequences.");
                            nr.Status = NetworkResultStatus.OOM;
                            break;
                        }

                        if (decodingOptions.BeamSearchSize > 1)
                        {
                            // Keep top N result and drop all others
                            for (int k = 0; k < batchSize; k++)
                            {
                                batch2beam2seq[k] = BeamSearch.GetTopNBSS(batch2beam2seq[k], decodingOptions.BeamSearchSize);
                            }
                        }


                        beam2batchStatus = Decoder.SwapBeamAndBatch(batch2beam2seq);
                        if (Decoder.AreAllSentsCompleted(beam2batchStatus))
                        {
                            break;
                        }
                    }

                    nr.Cost = 0.0f;
                    nr.Output = m_modelMetaData.TgtVocab.CovertToWords(beam2batchStatus);
                    if (decodingOptions.OutputAligmentsToSrc)
                    {
                        (nr.Alignments, nr.AlignmentScores) = Decoder.ExtractAlignments(beam2batchStatus);
                    }

                    if (cachedTensors != null)
                    {
                        foreach (var pair in cachedTensors)
                        {
                            pair.Value.Dispose();
                        }
                    }
                }
            }

            nr.RemoveDuplicatedEOS();

            nrs.Add(nr);
            return nrs;
        }

        public void DumpVocabToFiles(string outputSrcVocab, string outputTgtVocab)
        {
            m_modelMetaData.SrcVocab.DumpVocab(outputSrcVocab);
            m_modelMetaData.TgtVocab.DumpVocab(outputTgtVocab);
        }

        public void Test(string inputTestFile, string outputFile, int batchSize, DecodingOptions decodingOptions, string srcSpmPath, string tgtSpmPath, string outputAlignmentFile = null)
        {
            Test<Seq2SeqCorpusBatch>(inputTestFile, outputFile, batchSize, decodingOptions, srcSpmPath, tgtSpmPath, outputAlignmentFile);
        }

        public void Test(string inputTestFile, string inputPromptFile, string outputFile, int batchSize, DecodingOptions decodingOptions, string srcSpmPath, string tgtSpmPath, string outputAlignmentFile = null)
        {
            Test<Seq2SeqCorpusBatch>(inputTestFile, inputPromptFile, outputFile, batchSize, decodingOptions, srcSpmPath, tgtSpmPath, outputAlignmentFile);
        }
    }
}
