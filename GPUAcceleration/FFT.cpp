#include "Functions.h"
#include "liblion.h"
#include <iostream>
using namespace gtom;

__declspec(dllexport) void __stdcall FFT_CPU(float* data, float* result, int3 dims, int nthreads)
{
    relion::MultidimArray<double> Input;
    relion::MultidimArray<relion::Complex > Output;

    relion::FourierTransformer transformer;
    transformer.setThreadsNumber(nthreads);

    Input.initZeros(dims.z, dims.y, dims.x);
    memcpy(Input.data, data, Elements(dims) * sizeof(float));

    transformer.FourierTransform(Input, Output, false);

    memcpy(result, Output.data, ElementsFFT(dims) * sizeof(float2));

    transformer.cleanup();
}

__declspec(dllexport) void __stdcall IFFT_CPU(float* data, float* result, int3 dims, int nthreads)
{
    relion::MultidimArray<relion::Complex> Input;
    relion::MultidimArray<double> Output;

    relion::FourierTransformer transformer;
    transformer.setThreadsNumber(nthreads);

    Input.initZeros(dims.z, dims.y, dims.x / 2 + 1);
    memcpy(Input.data, data, ElementsFFT(dims) * sizeof(float2));
    Output.initZeros(dims.z, dims.y, dims.x);
    transformer.inverseFourierTransform(Input, Output);
    memcpy(result, Output.data, Elements(dims) * sizeof(float));
    transformer.cleanup();
    std::cout << "IFFT Done.\n";
}