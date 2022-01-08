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
  
}