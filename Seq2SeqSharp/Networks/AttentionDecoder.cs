﻿// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/Seq2SeqSharp
//
// This file is part of Seq2SeqSharp.
//
// Seq2SeqSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// Seq2SeqSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using Seq2SeqSharp.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using TensorSharp;

namespace Seq2SeqSharp
{


    [Serializable]
    public class AttentionDecoder : IDecoder
    {
        private readonly List<LSTMAttentionDecoderCell> m_decoders = new List<LSTMAttentionDecoderCell>();
        private readonly int m_hdim;
        private readonly int m_embDim;
        private readonly float m_dropoutRatio;
        private readonly int m_depth;
        private readonly int m_context;
        private readonly int m_deviceId;
        private readonly AttentionUnit m_attentionLayer;
        private readonly string m_name;
        private readonly bool m_enableCoverageModel;
        private readonly bool m_isTrainable;

        public AttentionDecoder(string name, int hiddenDim, int embeddingDim, int contextDim, float dropoutRatio, int depth, int deviceId, bool enableCoverageModel, bool isTrainable, DType elementType = DType.Float32)
        {
            m_name = name;
            m_hdim = hiddenDim;
            m_embDim = embeddingDim;
            m_context = contextDim;
            m_depth = depth;
            m_deviceId = deviceId;
            m_dropoutRatio = dropoutRatio;
            m_enableCoverageModel = enableCoverageModel;
            m_isTrainable = isTrainable;

            m_attentionLayer = new AttentionUnit($"{name}.AttnUnit", hiddenDim, contextDim, deviceId, enableCoverageModel, isTrainable: isTrainable, elementType: elementType);

            m_decoders.Add(new LSTMAttentionDecoderCell($"{name}.LSTMAttn_0", hiddenDim, embeddingDim, contextDim, deviceId, isTrainable, elementType: elementType));
            for (int i = 1; i < depth; i++)
            {
                m_decoders.Add(new LSTMAttentionDecoderCell($"{name}.LSTMAttn_{i}", hiddenDim, hiddenDim, contextDim, deviceId, isTrainable, elementType: elementType));
            }
        }

        public int GetDeviceId()
        {
            return m_deviceId;
        }

        public INeuralUnit CloneToDeviceAt(int deviceId)
        {
            return new AttentionDecoder(m_name, m_hdim, m_embDim, m_context, m_dropoutRatio, m_depth, deviceId, m_enableCoverageModel, m_isTrainable);
        }


        public void Reset(IWeightFactory weightFactory, int batchSize)
        {
            foreach (LSTMAttentionDecoderCell item in m_decoders)
            {
                item.Reset(weightFactory, batchSize);
            }
        }

        public AttentionPreProcessResult PreProcess(IWeightTensor encOutputs, int batchSize, IComputeGraph g)
        {
            return m_attentionLayer.PreProcess(encOutputs, batchSize, g);
        }


        public IWeightTensor Decode(IWeightTensor input, AttentionPreProcessResult attenPreProcessResult, int batchSize, IComputeGraph g)
        {
            IWeightTensor V = input;
            IWeightTensor lastStatus = m_decoders.LastOrDefault().Cell;
            IWeightTensor context = m_attentionLayer.Perform(lastStatus, attenPreProcessResult, batchSize, g);

            foreach (LSTMAttentionDecoderCell decoder in m_decoders)
            {
                IWeightTensor e = decoder.Step(context, V, g);
                V = e;
            }

            IWeightTensor eOutput = g.Dropout(V, m_dropoutRatio, false);

            return eOutput;
        }


        public List<IWeightTensor> GetCTs()
        {
            List<IWeightTensor> res = new List<IWeightTensor>();
            foreach (LSTMAttentionDecoderCell decoder in m_decoders)
            {
                res.Add(decoder.Cell);
            }

            return res;
        }

        public List<IWeightTensor> GetHTs()
        {
            List<IWeightTensor> res = new List<IWeightTensor>();
            foreach (LSTMAttentionDecoderCell decoder in m_decoders)
            {
                res.Add(decoder.Hidden);
            }

            return res;
        }

        public void SetCTs(List<IWeightTensor> l)
        {
            for (int i = 0; i < l.Count; i++)
            {
                m_decoders[i].Cell = l[i];
            }
        }

        public void SetHTs(List<IWeightTensor> l)
        {
            for (int i = 0; i < l.Count; i++)
            {
                m_decoders[i].Hidden = l[i];
            }
        }

        public List<IWeightTensor> GetParams()
        {
            List<IWeightTensor> response = new List<IWeightTensor>();

            foreach (LSTMAttentionDecoderCell item in m_decoders)
            {
                response.AddRange(item.getParams());
            }
            response.AddRange(m_attentionLayer.GetParams());

            return response;
        }

        public void Save(IModel stream)
        {
            m_attentionLayer.Save(stream);
            foreach (LSTMAttentionDecoderCell item in m_decoders)
            {
                item.Save(stream);
            }

       //     m_decoderFFLayer.Save(stream);
        }

        public void Load(IModel stream)
        {
            m_attentionLayer.Load(stream);
            foreach (LSTMAttentionDecoderCell item in m_decoders)
            {
                item.Load(stream);
            }

     //       m_decoderFFLayer.Load(stream);
        }
    }
}
