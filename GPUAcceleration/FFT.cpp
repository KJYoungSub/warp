#include "Functions.h"
#include "liblion.h"
#include <iostream>
using namespace gtom;

__declspec(dllexport) void __stdcall FFT_CPU(float* data, float* result, int3 dims, int nthreads)
{
    relion::MultidimArray<float> Input;
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
    relion::MultidimArray<float> Output;

    std::cout << "FFT 1\n";
    relion::FourierTransformer transformer;
    transformer.setThreadsNumber(nthreads);

    std::cout << "FFT 2\n";
    Input.initZeros(dims.z, dims.y, dims.x / 2 + 1);
    std::cout << "FFT 3\n";
    memcpy(Input.data, data, ElementsFFT(dims) * sizeof(float2));
    std::cout << "FFT 4\n";
    Output.initZeros(dims.z, dims.y, dims.x);
    std::cout << "FFT 5\n";
    transformer.inverseFourierTransform(Input, Output);
    std::cout << "FFT 6\n";
    memcpy(result, Output.data, Elements(dims) * sizeof(float));
    std::cout << "FFT 7\n";
    transformer.cleanup();
    std::cout << "FFT 8\n";
}