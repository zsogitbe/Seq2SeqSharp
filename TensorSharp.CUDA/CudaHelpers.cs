﻿using ManagedCuda.BasicTypes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TensorSharp.CUDA
{
    public static class CudaHelpers
    {
        public static CUdeviceptr GetBufferStart(Tensor tensor)
        {
            return ((CudaStorage)tensor.Storage).DevicePtrAtElement(tensor.StorageOffset);
        }

        public static void ThrowIfDifferentDevices(params Tensor[] tensors)
        {
            IEnumerable<Tensor> nonNull = tensors.Where(x => x != null);
            if (!nonNull.Any())
            {
                return;
            }

            int device = CudaHelpers.GetDeviceId(nonNull.First());

            if (nonNull.Any(x => CudaHelpers.GetDeviceId(x) != device))
            {
                throw new InvalidOperationException("All tensors must reside on the same device");
            }
        }

        public static int GetDeviceId(Tensor tensor)
        {
            return ((CudaStorage)tensor.Storage).DeviceId;
        }

        public static TSCudaContext TSContextForTensor(Tensor tensor)
        {
            return ((CudaStorage)tensor.Storage).TSContext;
        }
    }
}
