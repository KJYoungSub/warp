PROGRAM = GPUAcceleration.dll
NVCC = nvcc
NVCCFLAGS = --compiler-options "-fPIC -std=c++11 -mavx -march=native" -I../../liblion -I../GPUAcceleration/include -I../GPUAcceleration/einspline -lcufft -ltiff -lfftw3f

OBJS = ../GPUAcceleration/Angles.o ../GPUAcceleration/Comparison.o ../GPUAcceleration/Device.o ../GPUAcceleration/ParticleCTF.o \
	../GPUAcceleration/Polishing.o ../GPUAcceleration/SparseEigen.o ../GPUAcceleration/Backprojection.o \
	../GPUAcceleration/Correlation.o ../GPUAcceleration/Einspline.o ../GPUAcceleration/ParticleMultiBody.o \
	../GPUAcceleration/Post.o ../GPUAcceleration/Symmetry.o ../GPUAcceleration/Bessel.o ../GPUAcceleration/Cubic.o \
	../GPUAcceleration/FFT.o ../GPUAcceleration/ParticleNMA.o ../GPUAcceleration/Projector.o \
	../GPUAcceleration/BoxNet2.o ../GPUAcceleration/CubicGPU.o ../GPUAcceleration/FSC.o ../GPUAcceleration/ParticleShift.o \
	../GPUAcceleration/Raycast.o ../GPUAcceleration/Tools.o ../GPUAcceleration/CTF.o ../GPUAcceleration/Deconv.o ../GPUAcceleration/Memory.o \
	../GPUAcceleration/ParticleSoftBody.o ../GPUAcceleration/Shift.o ../GPUAcceleration/WeightOptimization.o \
	../GPUAcceleration/einspline/bspline_create.o ../GPUAcceleration/TiffNative.o \
	../../liblion/src/filename.o ../../liblion/src/backprojector.o ../../liblion/src/complex.o ../../liblion/src/ctf.o \
	../../liblion/src/euler.o ../../liblion/src/healpix_sampling.o ../../liblion/src/multidim_array.o \
	../../liblion/src/mask.o ../../liblion/src/projector.o ../../liblion/src/symmetries.o ../../liblion/src/funcs.o \
	../../liblion/src/numerical_recipes.o ../../liblion/src/fftw.o ../../liblion/src/transformations.o

%.o: %.cpp
	$(NVCC) $(NVCCFLAGS) -c -o $@ $<

%.o: %.cu
	$(NVCC) $(NVCCFLAGS) -c -o $@ $<

$(PROGRAM): $(OBJS)
	$(NVCC) $(NVCCFLAGS) --shared -o $(PROGRAM) ../../gtom/build/lib/libgtom.a /usr/lib/x86_64-linux-gnu/libfftw3f.a /usr/lib/x86_64-linux-gnu/libfftw3.a $^

.PHONY: clean
clean:
	rm -fr $(PROGRAM) $(OBJS)