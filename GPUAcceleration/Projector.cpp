#include "Functions.h"
#include "liblion.h"
using namespace gtom;

__declspec(dllexport) void __stdcall InitProjector(int3 dims, int oversampling, float* h_data, float* h_initialized, int projdim)
{
    relion::MultidimArray<double> dummy;
    relion::MultidimArray<double> vol;
    vol.initZeros(dims.z, dims.y, dims.x);

    for (uint i = 0; i < Elements(dims); i++)
        vol.data[i] = h_data[i];

    relion::Projector projector(dims.x, TRILINEAR, oversampling, 10, projdim);
    //projector.computeFourierTransformMap((relion::MultidimArray<double>&)vol, (relion::MultidimArray<double>&)dummy, -1, 1, true, false, false);
    projector.computeFourierTransformMap(vol, dummy, -1, 1, true, false, false);

    int3 projectordims = toInt3(XSIZE(projector.data), YSIZE(projector.data), ZSIZE(projector.data));
    for (uint i = 0; i < Elements(projectordims); i++)
        ((float2*)h_initialized)[i] = make_float2(projector.data.data[i].real, projector.data.data[i].imag);
}

__declspec(dllexport) void __stdcall SymmetrizeCPU(int3 dimsori, int3 dimspadded, int oversampling, float2* d_dataft, float* d_weights, char* c_symmetry)
{
    /*relion::FileName fn_symmetry(c_symmetry);

    relion::BackProjector backprojector(dimsori.x, 3, fn_symmetry, TRILINEAR, oversampling, 10, 0, 1.9, 15, 2);
    
    backprojector.initZeros(dimsori.x);
    int3 projectordims = toInt3(XSIZE(backprojector.data), YSIZE(backprojector.data), ZSIZE(backprojector.data));

    auto proj_data = backprojector.data;
    auto proj_weights = backprojector.weight;

    cudaMemcpy(proj_data.data, d_dataft, Elements(projectordims) * sizeof(float2), cudaMemcpyDeviceToHost);
    cudaMemcpy(proj_weights.data, d_weights, Elements(projectordims) * sizeof(float), cudaMemcpyDeviceToHost);

    backprojector.symmetrise(proj_data,
                            proj_weights,
                            backprojector.r_max * backprojector.r_max * oversampling * oversampling);

    cudaMemcpy(d_dataft, proj_data.data, Elements(projectordims) * sizeof(float2), cudaMemcpyHostToDevice);
    cudaMemcpy(d_weights, proj_weights.data, Elements(projectordims) * sizeof(float), cudaMemcpyHostToDevice);*/
}

__declspec(dllexport) void __stdcall BackprojectorReconstruct(int3 dimsori, int oversampling, float2* d_data, float* d_weights, char* c_symmetry, bool do_reconstruct_ctf, float* h_reconstruction)
{
    relion::FileName fn_symmetry(c_symmetry);

    relion::FourierTransformer transformer;
    transformer.setThreadsNumber(16);

    relion::BackProjector backprojector(dimsori.x, 3, fn_symmetry, TRILINEAR, oversampling, 10, 0, 1.9, 15, 2);
    backprojector.initZeros(dimsori.x);

    int3 projectordims = toInt3(XSIZE(backprojector.data), YSIZE(backprojector.data), ZSIZE(backprojector.data));

	cudaMemcpy(backprojector.data.data, d_data, Elements(projectordims) * sizeof(float2), cudaMemcpyDeviceToHost);
	cudaMemcpy(backprojector.weight.data, d_weights, Elements(projectordims) * sizeof(float), cudaMemcpyDeviceToHost);

	//WriteMRC(backprojector.weight.data, projectordims, "d_weightscopy.mrc");

    //memcpy(backprojector.data.data, h_data, Elements(projectordims) * sizeof(float2));
    //memcpy(backprojector.weight.data, h_weights, Elements(projectordims) * sizeof(float));

    relion::MultidimArray<double> vol, dummy;
    relion::MultidimArray<relion::Complex > F2D;
    relion::MultidimArray<double> fsc;
    fsc.resize(dimsori.x / 2 + 1);

    // backprojector.reconstruct((relion::MultidimArray<double>&)vol, 10, false, 1., (relion::MultidimArray<double>&)dummy, (relion::MultidimArray<double>&)dummy, (relion::MultidimArray<double>&)dummy, (relion::MultidimArray<double>&)fsc, 1., false, true, 16, -1);
    backprojector.reconstruct(vol, 10, false, 1., dummy, dummy, dummy, fsc, 1., false, true, 16, -1);

    if (do_reconstruct_ctf)
    {
        F2D.clear();
        transformer.FourierTransform(vol, F2D);
        
        FOR_ALL_DIRECT_ELEMENTS_IN_MULTIDIMARRAY(F2D)
        {
            h_reconstruction[n] = sqrt(F2D.data[n].real * F2D.data[n].real + F2D.data[n].imag * F2D.data[n].imag) * (DOUBLE)dimsori.x;
        }
    }
    else
    {
        memcpy(h_reconstruction, vol.data, Elements(dimsori) * sizeof(float));
    }
}

__declspec(dllexport) void __stdcall BackprojectorReconstructGPU(int3 dimsori, int3 dimspadded, int oversampling, float2* d_dataft, float* d_weights, char* c_symmetry, bool do_reconstruct_ctf, float* d_result, cufftHandle pre_planforw, cufftHandle pre_planback, cufftHandle pre_planforwctf, int griddingiterations)
{
	relion::FileName fn_symmetry(c_symmetry);

	if (fn_symmetry.compare("C1") != 0 && fn_symmetry.compare("c1") != 0)
	{
		relion::BackProjector backprojector(dimsori.x, 3, fn_symmetry, TRILINEAR, oversampling, 10, 0, 1.9, 15, 2);
		backprojector.initZeros(dimsori.x);
		int3 projectordims = toInt3(XSIZE(backprojector.data), YSIZE(backprojector.data), ZSIZE(backprojector.data));

		auto proj_data = backprojector.data;
		auto proj_weights = backprojector.weight;

		cudaMemcpy(proj_data.data, d_dataft, Elements(projectordims) * sizeof(float2), cudaMemcpyDeviceToHost);
		cudaMemcpy(proj_weights.data, d_weights, Elements(projectordims) * sizeof(float), cudaMemcpyDeviceToHost);
		//d_WriteMRC(d_weights, projectordims, "d_weights.mrc");

		//backprojector.enforceHermitianSymmetry(proj_data, proj_weights);

		backprojector.symmetrise(proj_data,
									proj_weights,
									backprojector.r_max * backprojector.r_max * oversampling * oversampling);

		cudaMemcpy(d_dataft, proj_data.data, Elements(projectordims) * sizeof(float2), cudaMemcpyHostToDevice);
		cudaMemcpy(d_weights, proj_weights.data, Elements(projectordims) * sizeof(float), cudaMemcpyHostToDevice);
		//d_WriteMRC(d_weights, projectordims, "d_weights_sym.mrc");
	}

    float* d_reconstructed;
    cudaMalloc((void**)&d_reconstructed, ElementsFFT(dimsori) * sizeof(float2));

    d_ReconstructGridding(d_dataft, d_weights, d_reconstructed, dimsori, dimspadded, oversampling, pre_planforw, pre_planback, griddingiterations);

    if (do_reconstruct_ctf)
    {
		float2* d_reconstructedft;
		cudaMalloc((void**)&d_reconstructedft, ElementsFFT(dimsori) * sizeof(float2));

        if (pre_planforwctf > NULL)
            d_FFTR2C(d_reconstructed, d_reconstructedft, &pre_planforwctf);
        else
            d_FFTR2C(d_reconstructed, d_reconstructedft, 3, dimsori);
        d_Abs(d_reconstructedft, d_result, ElementsFFT(dimsori));

		d_MultiplyByScalar(d_result, d_result, ElementsFFT(dimsori), 1.0f / Elements2(dimsori));

		cudaFree(d_reconstructedft);
    }
    else
    {
        cudaMemcpy(d_result, d_reconstructed, Elements(dimsori) * sizeof(float), cudaMemcpyDeviceToDevice);
    }

    cudaFree(d_reconstructed);
}