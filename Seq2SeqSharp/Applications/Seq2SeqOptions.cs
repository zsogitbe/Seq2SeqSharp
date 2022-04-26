﻿// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/Seq2SeqSharp
//
// This file is part of Seq2SeqSharp.
//
// Seq2SeqSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// Seq2SeqSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using AdvUtils;
using Seq2SeqSharp.Applications;

namespace Seq2SeqSharp
{
    public class Seq2SeqOptions : Options
    {
        [Arg("The network depth in decoder.", nameof(DecoderLayerDepth))]
        public int DecoderLayerDepth = 1;

        [Arg("Starting Learning rate factor for decoders", nameof(DecoderStartLearningRateFactor))]
        public float DecoderStartLearningRateFactor = 1.0f;

        [Arg("Decoder type: AttentionLSTM, Transformer", nameof(DecoderType))]
        public DecoderTypeEnums DecoderType = DecoderTypeEnums.Transformer;

        [Arg("Apply coverage model in decoder", nameof(EnableCoverageModel))]
        public bool EnableCoverageModel = false;

        [Arg("It indicates if the decoder is trainable", nameof(IsDecoderTrainable))]
        public bool IsDecoderTrainable = true;

        [Arg("It indicates if the src embedding is trainable", nameof(IsSrcEmbeddingTrainable))]
        public bool IsSrcEmbeddingTrainable = true;
        [Arg("It indicates if the tgt embedding is trainable", nameof(IsTgtEmbeddingTrainable))]
        public bool IsTgtEmbeddingTrainable = true;

        [Arg("Maxmium src sentence length in valid and test set", nameof(MaxTestSrcSentLength))]
        public int MaxTestSrcSentLength = 32;

        [Arg("Maxmium tgt sentence length in valid and test set", nameof(MaxTestTgtSentLength))]
        public int MaxTestTgtSentLength = 32;

        [Arg("Maxmium src sentence length in training corpus", nameof(MaxTrainSrcSentLength))]
        public int MaxTrainSrcSentLength = 110;

        [Arg("Maxmium tgt sentence length in training corpus", nameof(MaxTrainTgtSentLength))]
        public int MaxTrainTgtSentLength = 110;

        [Arg("The metric for sequence generation task. It supports BLEU and RougeL", nameof(SeqGenerationMetric))]
        public string SeqGenerationMetric = "BLEU";

        [Arg("Sharing embeddings between source side and target side", nameof(SharedEmbeddings))]
        public bool SharedEmbeddings = false;

        [Arg("The embedding dim in source side", nameof(SrcEmbeddingDim))]
        public int SrcEmbeddingDim = 128;

        [Arg("The embedding dim in target side", nameof(TgtEmbeddingDim))]
        public int TgtEmbeddingDim = 128;

        [Arg("It indicates if pointer generator is enabled or not for seq2seq tasks. It requires shared vocabulary between source and target", nameof(PointerGenerator))]
        public bool PointerGenerator = false;

        [Arg("The file path that outputs alignment to source sequence", nameof(OutputAlignmentsFile))]
        public string OutputAlignmentsFile = null;

        public DecodingOptions CreateDecodingOptions()
        {
            DecodingOptions decodingOptions = new DecodingOptions();
            decodingOptions.DecodingStrategy = DecodingStrategy;
            decodingOptions.TopPValue = DecodingTopPValue;
            decodingOptions.RepeatPenalty = DecodingRepeatPenalty;

            decodingOptions.BeamSearchSize = BeamSearchSize;

            decodingOptions.MaxSrcSentLength = MaxTestSrcSentLength;
            decodingOptions.MaxTgtSentLength = MaxTestTgtSentLength;

            decodingOptions.OutputAligmentsToSrc = !string.IsNullOrEmpty(OutputAlignmentsFile);

            return decodingOptions;
        }
    }
}
