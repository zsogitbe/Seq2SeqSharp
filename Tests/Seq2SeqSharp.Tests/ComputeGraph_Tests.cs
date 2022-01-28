﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Seq2SeqSharp.Applications;
using Seq2SeqSharp.Corpus;
using Seq2SeqSharp.Metrics;
using Seq2SeqSharp.Optimizer;
using Seq2SeqSharp.Tools;
using Seq2SeqSharp.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Seq2SeqSharp.Tests;

[TestClass]
public class ComputeGraph_Tests
{
    private WeightTensor BuildRandomTensor(long[] shape, string name, bool isTrainable = true)
    {
        var tensorA = new WeightTensor(shape, 1, 0, name: name, isTrainable: isTrainable);

        //Build test data and ground truth data
        float[] arrayA = new float[tensorA.ElementCount];
        Random rnd = new Random(DateTime.Now.Millisecond);
        for (int i = 0; i < tensorA.ElementCount; i++)
        {
            arrayA[i] = (float)rnd.NextDouble();
        }
        tensorA.SetWeightArray(arrayA);

        return tensorA;
    }

    private WeightTensor BuildRandomLabelTensor(int batchSize, int categoryNum, string name)
    {
        var tensorIdx = new WeightTensor(new long[] {batchSize, 1 }, 1, 0, name: name, isTrainable: false);

        //Build ground truth labels
        float[] arrayIdx = new float[batchSize];
        Random rnd = new Random(DateTime.Now.Millisecond);
        for (int i = 0; i < batchSize; i++)
        {
            arrayIdx[i] = rnd.Next(0, categoryNum);
        }
        tensorIdx.SetWeightArray(arrayIdx);

        return tensorIdx;
    }

    [TestMethod]
    public void TestAddTensorTensor()
    {
        TensorAllocator.InitDevices(ProcessorTypeEnums.CPU, new int[] { 0 });

        var graph = new ComputeGraphTensor(new WeightTensorFactory(), 0, true);

        var tensorA = new WeightTensor(new long[2] { 2, 2 }, 1, 0, name: "tensorA", isTrainable: true);
        var tensorB = new WeightTensor(new long[2] { 2, 2 }, 2, 0, name: "tensorB", isTrainable: true);

        var tensorSum = graph.Add(tensorA, tensorB);

        float v = tensorSum.GetWeightAt(new long[] { 1, 1 });

        Assert.IsTrue(v == 3.0f);

    }

    [TestMethod]
    public void TestAddTensorTensorBP()
    {
        TensorAllocator.InitDevices(ProcessorTypeEnums.CPU, new int[] { 0 });

        var graph = new ComputeGraphTensor(new WeightTensorFactory(), 0, true);

        var tensorA = new WeightTensor(new long[2] { 2, 2 }, 1, 0, name: "tensorA", isTrainable: true);
        var tensorB = new WeightTensor(new long[2] { 2, 2 }, 2, 0, name: "tensorB", isTrainable: true);

        var tensorSum = graph.Add(tensorA, tensorB);
        tensorSum.CopyWeightsToGradients(tensorSum);

        graph.Backward();


        float gA = tensorA.GetGradientAt(new long[] { 1, 1 });
        float gB = tensorB.GetGradientAt(new long[] { 1, 1, });

        Assert.IsTrue(gA == 3.0f);
        Assert.IsTrue(gB == 3.0f);
    }





    [TestMethod]
    public void TestAddSubGradients()
    {
        int batchSize = 5;
        int vocabSize = 20;

        TensorAllocator.InitDevices(ProcessorTypeEnums.CPU, new int[] { 0 });

        var graph = new ComputeGraphTensor(new WeightTensorFactory(), 0, true);

        var tensorA = new WeightTensor(new long[2] { batchSize, vocabSize }, 1, 0, name: "tensorA", isTrainable: true);
        var tensorB = new WeightTensor(new long[2] { batchSize, vocabSize }, 1, 0, name: "tensorB", isTrainable: true);
        var tensorIdx = BuildRandomLabelTensor(batchSize, vocabSize, "tensorIdx");

        var tensorANeg = graph.Mul(tensorA, -1.0f);
        var tensorANegSum = graph.Add(tensorANeg, 100.0f);
        var tensorSub = graph.Sub(100.0f, tensorB);

        float v1 = tensorANegSum.GetWeightAt(new long[] { 1, 1 });
        float v2 = tensorSub.GetWeightAt(new long[] { 1, 1 });

        Assert.IsTrue(v1 == v2);

        var softmax1 = graph.Softmax(tensorANegSum);
        var softmax2 = graph.Softmax(tensorSub);

        graph.CrossEntropyLoss(softmax1, tensorIdx);
        graph.CrossEntropyLoss(softmax2, tensorIdx);

        graph.Backward();

        float gA = tensorA.GetGradientAt(new long[] { 1, 1 });
        float gB = tensorB.GetGradientAt(new long[] { 1, 1, });

        Assert.IsTrue(gA == gB);
    }

    [TestMethod]
    public void TestCrossEntropyLoss()
    {
        int batchSize = 5;
        int vocabSize = 20;
        TensorAllocator.InitDevices(ProcessorTypeEnums.CPU, new int[] { 0 });
        var graph = new ComputeGraphTensor(new WeightTensorFactory(), 0, true);

        var tensorA = BuildRandomTensor(shape: new long[2] { batchSize, vocabSize }, name: "tensorA", isTrainable: true);
        var tensorIdx = BuildRandomLabelTensor(batchSize, vocabSize, "tensorIdx");

        var probs = graph.Softmax(tensorA);
        float[] softmaxWeights = probs.ToWeightArray();
        graph.CrossEntropyLoss(probs, tensorIdx, avgLoss: false);

        graph.Backward();

        //Check if graidents are correct
        for (int i = 0; i < batchSize; i++)
        {
            for (int j = 0; j < vocabSize; j++)
            {
                float softmaxWeight = softmaxWeights[i * vocabSize + j];
                float tensorAGrad = tensorA.GetGradientAt(new long[] { i, j });

                if (tensorIdx.GetWeightAt(new long[] {i, 0}) != j)
                {
                    Assert.IsTrue(Math.Round(tensorAGrad, 5) == Math.Round(softmaxWeight, 5));
                }
                else
                {
                    Assert.IsTrue(Math.Round(tensorAGrad, 5) == Math.Round(softmaxWeight - 1.0f, 5));
                }
            }
        }
    }  
}