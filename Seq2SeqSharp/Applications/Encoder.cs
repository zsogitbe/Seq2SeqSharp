﻿using Seq2SeqSharp.Corpus;
using Seq2SeqSharp.Tools;
using Seq2SeqSharp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Seq2SeqSharp.Applications
{
    public class Encoder
    {
        static List<List<string>> InsertCLSToken(List<List<string>> tokens)
        {
            List<List<string>> newTokens = new List<List<string>>();

            foreach (var item in tokens)
            {
                List<string> r = new List<string>
                {
                    BuildInTokens.CLS
                };
                r.AddRange(item);

                newTokens.Add(r);

            }

            return newTokens;
        }

        static public IWeightTensor Run(IComputeGraph computeGraph, ISntPairBatch sntPairBatch, IEncoder encoder, IModel modelMetaData, ShuffleEnums shuffleType,
            IWeightTensor srcEmbedding, IWeightTensor posEmbedding, IWeightTensor segmentEmbedding, List<List<string>> srcSnts, List<int> originalSrcLengths, bool applyContextEmbeddingsToEntireSequence = true)
        {
            int batchSize = srcSnts.Count;

            // Reset networks
            encoder.Reset(computeGraph.GetWeightFactory(), srcSnts.Count);

            //Build contextual feature if they exist
            IWeightTensor contextTensor = null;
            for (int i = 1; i < sntPairBatch.GetSrcGroupSize(); i++)
            {
                var contextCLSOutput = BuildTensorForSourceTokenGroupAt(computeGraph, sntPairBatch, encoder, modelMetaData, srcEmbedding, posEmbedding, segmentEmbedding, i);
                if (contextTensor == null)
                {
                    contextTensor = contextCLSOutput;
                }
                else
                {
                    contextTensor = computeGraph.Add(contextTensor, contextCLSOutput);
                }
            }


            IWeightTensor encOutput = InnerRunner(computeGraph, srcSnts, originalSrcLengths, shuffleType, encoder, modelMetaData, srcEmbedding, posEmbedding, segmentEmbedding, contextTensor, applyContextEmbeddingsToEntireSequence);
            return encOutput;
        }

        public static IWeightTensor BuildTensorForSourceTokenGroupAt(IComputeGraph computeGraph, ISntPairBatch sntPairBatch, IEncoder encoder, IModel modelMetaData, IWeightTensor srcEmbedding, IWeightTensor posEmbedding, IWeightTensor segmentEmbedding, int i)
        {
            var contextTokens = InsertCLSToken(sntPairBatch.GetSrcTokens(i));
            var originalSrcContextLength = BuildInTokens.PadSentences(contextTokens);
            IWeightTensor encContextOutput = InnerRunner(computeGraph, contextTokens, originalSrcContextLength, ShuffleEnums.Random, encoder, modelMetaData, srcEmbedding, posEmbedding, segmentEmbedding);

            int contextPaddedLen = contextTokens[0].Count;
            float[] contextCLSIdxs = new float[sntPairBatch.BatchSize];
            for (int j = 0; j < sntPairBatch.BatchSize; j++)
            {
                contextCLSIdxs[j] = j * contextPaddedLen;
            }

            IWeightTensor contextCLSOutput = computeGraph.IndexSelect(encContextOutput, contextCLSIdxs);
            return contextCLSOutput;
        }

        static private IWeightTensor InnerRunner(IComputeGraph computeGraph, List<List<string>> srcSnts, List<int> originalSrcLengths, ShuffleEnums shuffleType, IEncoder encoder, IModel modelMetaData,
           IWeightTensor srcEmbedding, IWeightTensor posEmbedding, IWeightTensor segmentEmbedding, IWeightTensor contextEmbeddings = null, bool applyContextEmbeddingsToEntireSequence = true)
        {

            int srcSeqPaddedLen = srcSnts[0].Count;
            IWeightTensor srcSelfMask = shuffleType == ShuffleEnums.NoPaddingInSrc ? null : computeGraph.BuildPadSelfMask(srcSeqPaddedLen, originalSrcLengths); // The length of source sentences are same in a single mini-batch, so we don't have source mask.

            // Encoding input source sentences
            var srcTokensList = modelMetaData.SrcVocab.GetWordIndex(srcSnts);
            var encOutput = RunEncoder(computeGraph, srcTokensList, encoder, modelMetaData, srcEmbedding, srcSelfMask, posEmbedding, originalSrcLengths, segmentEmbedding, contextEmbeddings, applyContextEmbeddingsToEntireSequence);
            if (srcSelfMask != null)
            {
                srcSelfMask.Dispose();
            }

            return encOutput;
        }


        /// <summary>
        /// Encode source sentences and output encoded weights
        /// </summary>
        /// <param name="g"></param>
        /// <param name="seqs"></param>
        /// <param name="encoder"></param>
        /// <param name="reversEncoder"></param>
        /// <param name="embeddings"></param>
        /// <returns></returns>
        static private IWeightTensor RunEncoder(IComputeGraph g, List<List<int>> seqs, IEncoder encoder, IModel modelMetaData, IWeightTensor embeddings, IWeightTensor selfMask, IWeightTensor posEmbeddings, List<int> seqOriginalLengths, 
            IWeightTensor segmentEmbeddings, IWeightTensor contextEmbeddings, bool applyContextEmbeddingsToEntireSequence = true)
        {
            int batchSize = seqs.Count;
            var inputEmbs = TensorUtils.CreateTokensEmbeddings(seqs, g, embeddings, seqOriginalLengths, segmentEmbeddings, contextEmbeddings, modelMetaData.SrcVocab, applyContextEmbeddingsToEntireSequence: applyContextEmbeddingsToEntireSequence, (float)Math.Sqrt(embeddings.Columns));

            if (modelMetaData.EncoderType == EncoderTypeEnums.Transformer)
            {
                inputEmbs = PositionEmbedding.AddPositionEmbedding(g, posEmbeddings, batchSize, inputEmbs, 0.0f);
            }

            return encoder.Encode(inputEmbs, batchSize, g, selfMask);
        }
    }
}
