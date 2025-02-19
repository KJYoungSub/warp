﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;
using System.Xml.XPath;
using Accord;
using Accord.Math.Optimization;
using MathNet.Numerics;
using Warp.Headers;
using Warp.Sociology;
using Warp.Tools;

namespace Warp
{
    public class TiltSeries : Movie
    {
        #region Directories

        public string ReconstructionDir => DirectoryName + "reconstruction/";

        public string ReconstructionDeconvDir => ReconstructionDir + "deconv/";

        public string ReconstructionOddDir => ReconstructionDir + "odd/";

        public string ReconstructionEvenDir => ReconstructionDir + "even/";

        public string SubtomoDir => DirectoryName + "subtomo/" + RootName + "/";

        public string ParticleSeriesDir => DirectoryName + "particleseries/" + RootName + "/";

        public string WeightOptimizationDir => DirectoryName + "weightoptimization/";

        #endregion

        #region Runtime dimensions

        /// <summary>
        /// These must be populated before most operations, otherwise exceptions will be thrown.
        /// Not an elegant solution, but it avoids passing them to a lot of methods.
        /// Given in Angstrom.
        /// </summary>
        public float3 VolumeDimensionsPhysical;

        /// <summary>
        /// Used to account for rounding the size of downsampled raw images to multiples of 2
        /// </summary>
        public float3 SizeRoundingFactors = new float3(1, 1, 1);

        #endregion

        private bool _AreAnglesInverted = false;
        public bool AreAnglesInverted
        {
            get { return _AreAnglesInverted; }
            set { if (value != _AreAnglesInverted) { _AreAnglesInverted = value; OnPropertyChanged(); } }
        }

        public float3 PlaneNormal;

        #region Grids

        private LinearGrid4D _GridVolumeWarpX = new LinearGrid4D(new int4(1, 1, 1, 1));
        public LinearGrid4D GridVolumeWarpX
        {
            get { return _GridVolumeWarpX; }
            set { if (value != _GridVolumeWarpX) { _GridVolumeWarpX = value; OnPropertyChanged(); } }
        }

        private LinearGrid4D _GridVolumeWarpY = new LinearGrid4D(new int4(1, 1, 1, 1));
        public LinearGrid4D GridVolumeWarpY
        {
            get { return _GridVolumeWarpY; }
            set { if (value != _GridVolumeWarpY) { _GridVolumeWarpY = value; OnPropertyChanged(); } }
        }

        private LinearGrid4D _GridVolumeWarpZ = new LinearGrid4D(new int4(1, 1, 1, 1));
        public LinearGrid4D GridVolumeWarpZ
        {
            get { return _GridVolumeWarpZ; }
            set { if (value != _GridVolumeWarpZ) { _GridVolumeWarpZ = value; OnPropertyChanged(); } }
        }

        #endregion

        #region Per-tilt CTF data

        private ObservableCollection<float2[]> _TiltPS1D = new ObservableCollection<float2[]>();
        public ObservableCollection<float2[]> TiltPS1D
        {
            get { return _TiltPS1D; }
            set { if (value != _TiltPS1D) { _TiltPS1D = value; OnPropertyChanged(); } }
        }

        private ObservableCollection<Cubic1D> _TiltSimulatedBackground = new ObservableCollection<Cubic1D>();
        public ObservableCollection<Cubic1D> TiltSimulatedBackground
        {
            get { return _TiltSimulatedBackground; }
            set { if (value != _TiltSimulatedBackground) { _TiltSimulatedBackground = value; OnPropertyChanged(); } }
        }

        private ObservableCollection<Cubic1D> _TiltSimulatedScale = new ObservableCollection<Cubic1D>();
        public ObservableCollection<Cubic1D> TiltSimulatedScale
        {
            get { return _TiltSimulatedScale; }
            set { if (value != _TiltSimulatedScale) { _TiltSimulatedScale = value; OnPropertyChanged(); } }
        }

        public float GetTiltDefocus(int tiltID)
        {
            if (GridCTFDefocus != null && GridCTFDefocus.FlatValues.Length > tiltID)
                return GridCTFDefocus.FlatValues[tiltID];
            return 0;
        }

        public float GetTiltDefocusDelta(int tiltID)
        {
            if (GridCTFDefocusDelta != null && GridCTFDefocusDelta.FlatValues.Length > tiltID)
                return GridCTFDefocusDelta.FlatValues[tiltID];
            return 0;
        }

        public float GetTiltDefocusAngle(int tiltID)
        {
            if (GridCTFDefocusAngle != null && GridCTFDefocusAngle.FlatValues.Length > tiltID)
                return GridCTFDefocusAngle.FlatValues[tiltID];
            return 0;
        }

        public float GetTiltPhase(int tiltID)
        {
            if (GridCTFPhase != null && GridCTFPhase.FlatValues.Length > tiltID)
                return GridCTFPhase.FlatValues[tiltID];
            return 0;
        }

        public CTF GetTiltCTF(int tiltID)
        {
            CTF Result = CTF.GetCopy();
            Result.Defocus = (decimal)GetTiltDefocus(tiltID);
            Result.DefocusDelta = (decimal)GetTiltDefocusDelta(tiltID);
            Result.DefocusAngle = (decimal)GetTiltDefocusAngle(tiltID);
            Result.PhaseShift = (decimal)GetTiltPhase(tiltID);

            return Result;
        }

        public float2[] GetTiltSimulated1D(int tiltID)
        {
            if (TiltPS1D.Count <= tiltID ||
                TiltPS1D[tiltID] == null ||
                TiltSimulatedScale.Count <= tiltID ||
                TiltSimulatedScale[tiltID] == null)
                return null;

            CTF TiltCTF = GetTiltCTF(tiltID);

            float[] SimulatedCTF = TiltCTF.Get1DWithIce(TiltPS1D[tiltID].Length, true);

            float2[] Result = new float2[SimulatedCTF.Length];
            for (int i = 0; i < Result.Length; i++)
                Result[i] = new float2(TiltPS1D[tiltID][i].X, SimulatedCTF[i] *
                                                              TiltSimulatedScale[tiltID].Interp(TiltPS1D[tiltID][i].X));

            return Result;
        }

        public event Action TiltCTFProcessed;

        #endregion

        #region Per-tilt parameters

        public float[] Angles = { 0 };
        public float[] Dose = { 0 };
        public bool[] UseTilt = { true };

        public float[] TiltAxisAngles = { 0 };
        public float[] TiltAxisOffsetX = { 0 };
        public float[] TiltAxisOffsetY = { 0 };
        public string[] TiltMoviePaths = { "" };

        public int[] IndicesSortedAngle
        {
            get
            {
                if (Angles == null)
                    return null;

                List<int> Sorted = new List<int>(Angles.Length);
                for (int i = 0; i < Angles.Length; i++)
                    Sorted.Add(i);

                Sorted.Sort((a, b) => Angles[a].CompareTo(Angles[b]));

                return Sorted.ToArray();
            }
        }

        public int[] IndicesSortedAbsoluteAngle
        {
            get
            {
                if (Angles == null)
                    return null;

                List<int> Sorted = new List<int>(Angles.Length);
                for (int i = 0; i < Angles.Length; i++)
                    Sorted.Add(i);

                Sorted.Sort((a, b) => Math.Abs(Angles[a]).CompareTo(Math.Abs(Angles[b])));

                return Sorted.ToArray();
            }
        }

        private int[] _IndicesSortedDose;
        public int[] IndicesSortedDose
        {
            get
            {
                if (Dose == null)
                    return null;

                if (_IndicesSortedDose == null)
                {
                    List<int> Sorted = new List<int>(Dose.Length);
                    for (int i = 0; i < Dose.Length; i++)
                        Sorted.Add(i);

                    Sorted.Sort((a, b) => Dose[a].CompareTo(Dose[b]));

                    _IndicesSortedDose = Sorted.ToArray();
                }

                return _IndicesSortedDose;
            }
        }

        public int NUniqueTilts
        {
            get
            {
                HashSet<float> UniqueAngles = new HashSet<float>();
                foreach (var angle in Angles)
                    if (!UniqueAngles.Contains(angle))
                        UniqueAngles.Add(angle);

                return UniqueAngles.Count;
            }
        }

        public int NTilts => Angles.Length;

        public float MinTilt => MathHelper.Min(Angles);
        public float MaxTilt => MathHelper.Max(Angles);

        public float MinDose => MathHelper.Min(Dose);
        public float MaxDose => MathHelper.Max(Dose);

        #endregion

        public TiltSeries(string path) : base(path)
        {
            // XML loading is done in base constructor

            if (Angles.Length <= 1)   // In case angles and dose haven't been read and stored in .xml yet.
            {
                InitializeFromTomoStar(new Star(path));
            }
        }

        public void InitializeFromTomoStar(Star table)
        {
            if (!table.HasColumn("wrpDose") || !table.HasColumn("wrpAngleTilt"))
                throw new Exception("STAR file has no wrpDose or wrpTilt column.");

            List<float> TempAngles = new List<float>();
            List<float> TempDose = new List<float>();
            List<float> TempAxisAngles = new List<float>();
            List<float> TempOffsetX = new List<float>();
            List<float> TempOffsetY = new List<float>();
            List<string> TempMoviePaths = new List<string>();

            for (int i = 0; i < table.RowCount; i++)
            {
                TempAngles.Add(float.Parse(table.GetRowValue(i, "wrpAngleTilt")));
                TempDose.Add(float.Parse(table.GetRowValue(i, "wrpDose")));

                if (table.HasColumn("wrpAxisAngle"))
                    TempAxisAngles.Add(float.Parse(table.GetRowValue(i, "wrpAxisAngle"), CultureInfo.InvariantCulture));
                else
                    TempAxisAngles.Add(0);

                if (table.HasColumn("wrpAxisOffsetX") && table.HasColumn("wrpAxisOffsetY"))
                {
                    TempOffsetX.Add(float.Parse(table.GetRowValue(i, "wrpAxisOffsetX"), CultureInfo.InvariantCulture));
                    TempOffsetY.Add(float.Parse(table.GetRowValue(i, "wrpAxisOffsetY"), CultureInfo.InvariantCulture));
                }
                else
                {
                    TempOffsetX.Add(0);
                    TempOffsetY.Add(0);
                }

                if (table.HasColumn("wrpMovieName"))
                    TempMoviePaths.Add(table.GetRowValue(i, "wrpMovieName"));
            }

            if (TempAngles.Count == 0 || TempMoviePaths.Count == 0)
                throw new Exception("Metadata must contain at least 3 values per tilt: movie paths, tilt angles, and accumulated dose.");

            Angles = TempAngles.ToArray();
            Dose = TempDose.ToArray();
            TiltAxisAngles = TempAxisAngles.Count > 0 ? TempAxisAngles.ToArray() : Helper.ArrayOfConstant(0f, NTilts);
            TiltAxisOffsetX = TempOffsetX.Count > 0 ? TempOffsetX.ToArray() : Helper.ArrayOfConstant(0f, NTilts);
            TiltAxisOffsetY = TempOffsetY.Count > 0 ? TempOffsetY.ToArray() : Helper.ArrayOfConstant(0f, NTilts);

            TiltMoviePaths = TempMoviePaths.ToArray();

            UseTilt = Helper.ArrayOfConstant(true, NTilts);
        }

        #region Processing tasks

        #region CTF fitting

        public void ProcessCTFSimultaneous(ProcessingOptionsMovieCTF options)
        {
            IsProcessing = true;

            if (!Directory.Exists(PowerSpectrumDir))
                Directory.CreateDirectory(PowerSpectrumDir);

            int2 DimsFrame;
            {
                MapHeader HeaderMovie = MapHeader.ReadFromFile(DirectoryName + TiltMoviePaths[0]);
                DimsFrame = new int2(new float2(HeaderMovie.Dimensions.X, HeaderMovie.Dimensions.Y) / (float)options.DownsampleFactor + 1) / 2 * 2;
            }

            #region Dimensions and grids

            int NFrames = NTilts;
            int2 DimsImage = DimsFrame;
            int2 DimsRegionBig = new int2(1536);
            int2 DimsRegion = new int2(options.Window, options.Window);

            float OverlapFraction = 0.5f;
            int2 DimsPositionGrid;
            int3[] PositionGrid = Helper.GetEqualGridSpacing(DimsImage, new int2(DimsRegionBig.X, DimsRegionBig.Y), OverlapFraction, out DimsPositionGrid);
            float3[] PositionGridPhysical = PositionGrid.Select(v => new float3(v.X + DimsRegionBig.X / 2 - DimsImage.X / 2,
                                                                                v.Y + DimsRegionBig.Y / 2 - DimsImage.Y / 2,
                                                                                0) *
                                                                                (float)options.BinnedPixelSizeMean * 1e-4f).ToArray();
            int NPositions = (int)DimsPositionGrid.Elements();

            bool CTFSpace = true;
            bool CTFTime = false;
            int3 CTFSpectraGrid = new int3(DimsPositionGrid.X, DimsPositionGrid.Y, NFrames);

            int MinFreqInclusive = (int)(options.RangeMin * DimsRegion.X / 2);
            int MaxFreqExclusive = (int)(options.RangeMax * DimsRegion.X / 2);
            int NFreq = MaxFreqExclusive - MinFreqInclusive;

            #endregion

            #region Allocate memory

            // GPU
            Image CTFSpectra = new Image(IntPtr.Zero, new int3(DimsRegion.X, DimsRegion.X, (int)CTFSpectraGrid.Elements()), true);
            Image CTFMean = new Image(new int3(DimsRegion), true);
            Image CTFCoordsCart = new Image(new int3(DimsRegion), true, true);
            Image CTFCoordsPolarTrimmed = new Image(new int3(NFreq, DimsRegion.X, 1), false, true);

            // CPU
            float2[] GlobalPS1D = null;
            float[][] LocalPS1D = new float[NPositions * NFrames][];
            Cubic1D GlobalBackground = null, GlobalScale = null;
            CTF GlobalCTF = null;
            float2 GlobalPlaneAngle = new float2();

            #endregion

            #region Helper methods

            Func<float[], float[]> GetDefocusGrid = (defoci) =>
            {
                float[] Result = new float[NPositions * NFrames];

                for (int t = 0; t < NFrames; t++)
                {
                    float3 Normal = (Matrix3.RotateX(GlobalPlaneAngle.X * Helper.ToRad) * Matrix3.RotateY(GlobalPlaneAngle.Y * Helper.ToRad)) * new float3(0, 0, 1);
                    Normal = Matrix3.Euler(0, Angles[t] * (AreAnglesInverted ? -1 : 1) * Helper.ToRad, 0) * Normal;
                    Normal = Matrix3.Euler(0, 0, -TiltAxisAngles[t] * Helper.ToRad) * Normal;
                    for (int i = 0; i < NPositions; i++)
                        Result[t * NPositions + i] = defoci[t] - float3.Dot(Normal, PositionGridPhysical[i]) / Normal.Z;
                }

                return Result;
            };

            #region Background fitting methods

            Action UpdateBackgroundFit = () =>
            {
                float2[] ForPS1D = GlobalPS1D.Skip(Math.Max(5, MinFreqInclusive / 1)).ToArray();
                Cubic1D.FitCTF(ForPS1D,
                               GlobalCTF.Get1DWithIce(GlobalPS1D.Length, true, true).Skip(Math.Max(5, MinFreqInclusive / 1)).ToArray(),
                               GlobalCTF.GetZeros(),
                               GlobalCTF.GetPeaks(),
                               out GlobalBackground,
                               out GlobalScale);
            };

            Action<bool> UpdateRotationalAverage = keepbackground =>
            {
                float[] MeanData = CTFMean.GetHost(Intent.Read)[0];

                Image CTFMeanCorrected = new Image(new int3(DimsRegion), true);
                float[] MeanCorrectedData = CTFMeanCorrected.GetHost(Intent.Write)[0];

                // Subtract current background estimate from spectra, populate coords.
                Helper.ForEachElementFT(DimsRegion,
                                        (x, y, xx, yy, r, a) =>
                                        {
                                            int i = y * (DimsRegion.X / 2 + 1) + x;
                                            MeanCorrectedData[i] = MeanData[i] - GlobalBackground.Interp(r / DimsRegion.X);
                                        });

                Image CTFAverage1D = new Image(IntPtr.Zero, new int3(DimsRegion.X / 2, 1, 1));

                GPU.CTFMakeAverage(CTFMeanCorrected.GetDevice(Intent.Read),
                                   CTFCoordsCart.GetDevice(Intent.Read),
                                   (uint)CTFMeanCorrected.DimsEffective.ElementsSlice(),
                                   (uint)DimsRegion.X,
                                   new[] { GlobalCTF.ToStruct() },
                                   GlobalCTF.ToStruct(),
                                   0,
                                   (uint)DimsRegion.X / 2,
                                   1,
                                   CTFAverage1D.GetDevice(Intent.Write));

                //CTFAverage1D.WriteMRC("CTFAverage1D.mrc");

                float[] RotationalAverageData = CTFAverage1D.GetHost(Intent.Read)[0];
                float2[] ForPS1D = new float2[GlobalPS1D.Length];
                if (keepbackground)
                    for (int i = 0; i < ForPS1D.Length; i++)
                        ForPS1D[i] = new float2((float)i / DimsRegion.X, RotationalAverageData[i] + GlobalBackground.Interp((float)i / DimsRegion.X));
                else
                    for (int i = 0; i < ForPS1D.Length; i++)
                        ForPS1D[i] = new float2((float)i / DimsRegion.X, RotationalAverageData[i]);
                MathHelper.UnNaN(ForPS1D);

                GlobalPS1D = ForPS1D;

                CTFMeanCorrected.Dispose();
                CTFAverage1D.Dispose();
            };

            #endregion

            #endregion

            // Extract movie regions, create individual spectra in Cartesian coordinates and their mean.

            #region Create spectra

            int PlanForw = GPU.CreateFFTPlan(new int3(DimsRegionBig), (uint)NPositions);
            int PlanBack = GPU.CreateIFFTPlan(new int3(DimsRegion), (uint)NPositions);

            Movie[] TiltMovies;
            Image[] TiltMovieData;
            LoadMovieData(options, true, out TiltMovies, out TiltMovieData);

            for (int t = 0; t < NTilts; t++)
            {
                Image TiltMovieAverage = TiltMovieData[t];

                GPU.Normalize(TiltMovieAverage.GetDevice(Intent.Read),
                              TiltMovieAverage.GetDevice(Intent.Write),
                              (uint)TiltMovieAverage.ElementsReal,
                              1);

                Image MovieCTFMean = new Image(new int3(DimsRegion), true);

                GPU.CreateSpectra(TiltMovieAverage.GetDevice(Intent.Read),
                                  DimsImage,
                                  TiltMovieAverage.Dims.Z,
                                  PositionGrid,
                                  NPositions,
                                  DimsRegionBig,
                                  CTFSpectraGrid.Slice(),
                                  DimsRegion,
                                  CTFSpectra.GetDeviceSlice(t * (int)CTFSpectraGrid.ElementsSlice(), Intent.Write),
                                  MovieCTFMean.GetDevice(Intent.Write),
                                  PlanForw,
                                  PlanBack);

                CTFMean.Add(MovieCTFMean);

                MovieCTFMean.Dispose();
                TiltMovieAverage.FreeDevice();
            }

            GPU.DestroyFFTPlan(PlanBack);
            GPU.DestroyFFTPlan(PlanForw);

            CTFMean.Multiply(1f / NTilts);

            #endregion

            // Populate address arrays for later.

            #region Init addresses

            {
                float2[] CoordsData = new float2[CTFCoordsCart.ElementsSliceComplex];

                Helper.ForEachElementFT(DimsRegion, (x, y, xx, yy, r, a) => CoordsData[y * (DimsRegion.X / 2 + 1) + x] = new float2(r, a));
                CTFCoordsCart.UpdateHostWithComplex(new[] { CoordsData });

                CoordsData = new float2[NFreq * DimsRegion.X];
                Helper.ForEachElement(CTFCoordsPolarTrimmed.DimsSlice, (x, y) =>
                {
                    float Angle = (float)y / DimsRegion.X * (float)Math.PI;
                    float Ny = 1f / DimsRegion.X;
                    CoordsData[y * NFreq + x] = new float2((x + MinFreqInclusive) * Ny, Angle);
                });
                CTFCoordsPolarTrimmed.UpdateHostWithComplex(new[] { CoordsData });
            }

            #endregion

            #region Initial 1D spectra

            // Mean spectrum to fit background
            {
                Image CTFAverage1D = new Image(IntPtr.Zero, new int3(DimsRegion.X / 2, 1, 1));

                GPU.CTFMakeAverage(CTFMean.GetDevice(Intent.Read),
                                   CTFCoordsCart.GetDevice(Intent.Read),
                                   (uint)CTFMean.ElementsSliceReal,
                                   (uint)DimsRegion.X,
                                   new[] { new CTF().ToStruct() },
                                   new CTF().ToStruct(),
                                   0,
                                   (uint)DimsRegion.X / 2,
                                   1,
                                   CTFAverage1D.GetDevice(Intent.Write));

                //CTFAverage1D.WriteMRC("CTFAverage1D.mrc");

                float[] CTFAverage1DData = CTFAverage1D.GetHost(Intent.Read)[0];
                float2[] ForPS1D = new float2[DimsRegion.X / 2];
                for (int i = 0; i < ForPS1D.Length; i++)
                    ForPS1D[i] = new float2((float)i / DimsRegion.X, (float)Math.Round(CTFAverage1DData[i], 4));
                GlobalPS1D = ForPS1D;

                CTFAverage1D.Dispose();
            }

            // Individual 1D spectra for initial grid search below
            {
                Image CTFAverage1D = new Image(IntPtr.Zero, new int3(DimsRegion.X / 2, 1, 1));

                for (int s = 0; s < NPositions * NFrames; s++)
                {
                    GPU.CTFMakeAverage(CTFSpectra.GetDeviceSlice(s, Intent.Read),
                                       CTFCoordsCart.GetDevice(Intent.Read),
                                       (uint)CTFMean.ElementsSliceReal,
                                       (uint)DimsRegion.X,
                                       new[] { new CTF().ToStruct() },
                                       new CTF().ToStruct(),
                                       0,
                                       (uint)DimsRegion.X / 2,
                                       1,
                                       CTFAverage1D.GetDevice(Intent.Write));

                    //CTFAverage1D.WriteMRC("CTFAverage1D.mrc");

                    LocalPS1D[s] = CTFAverage1D.GetHostContinuousCopy();
                }

                CTFAverage1D.Dispose();
            }

            #endregion

            #region Do initial fit on mean 1D PS
            {
                float2[] ForPS1D = GlobalPS1D.Skip(MinFreqInclusive).Take(Math.Max(2, NFreq * 2 / 3)).ToArray();

                float[] CurrentBackground;

                // Get a very rough background spline fit with 3-5 nodes
                int NumNodes = Math.Max(3, (int)((options.RangeMax - options.RangeMin) * 5M * 2 / 3));
                GlobalBackground = Cubic1D.Fit(ForPS1D, NumNodes);

                CurrentBackground = GlobalBackground.Interp(ForPS1D.Select(p => p.X).ToArray());
                float[][] SubtractedLocal1D = new float[LocalPS1D.Length][];
                for (int s = 0; s < LocalPS1D.Length; s++)
                {
                    SubtractedLocal1D[s] = new float[NFreq * 2 / 3];
                    for (int f = 0; f < NFreq * 2 / 3; f++)
                        SubtractedLocal1D[s][f] = LocalPS1D[s][f + MinFreqInclusive] - CurrentBackground[f];
                }

                float[] GridDeltas = GetDefocusGrid(Helper.ArrayOfConstant(0f, NFrames));

                float ZMin = (float)options.ZMin;
                float ZMax = (float)options.ZMax;
                float PhaseMin = 0f;
                float PhaseMax = options.DoPhase ? 1f : 0f;

                float ZStep = Math.Max(0.01f, (ZMax - ZMin) / 200f);

                float BestZ = 0, BestPhase = 0, BestScore = -999;
                Parallel.For(0, (int)((ZMax - ZMin + ZStep - 1e-6f) / ZStep), zi =>
                {
                    float z = ZMin + zi * ZStep;

                    for (float p = PhaseMin; p <= PhaseMax; p += 0.01f)
                    {
                        float Score = 0;

                        for (int s = 0; s < NPositions * NFrames; s++)
                        {
                            CTF CurrentParams = new CTF
                            {
                                PixelSize = options.BinnedPixelSizeMean,

                                Defocus = (decimal)(z + GridDeltas[s]),
                                PhaseShift = (decimal)p,

                                Cs = options.Cs,
                                Voltage = options.Voltage,
                                Amplitude = options.Amplitude
                            };
                            float[] SimulatedCTF = CurrentParams.Get1D(GlobalPS1D.Length, true).Skip(MinFreqInclusive).Take(Math.Max(2, NFreq * 2 / 3)).ToArray();
                            MathHelper.NormalizeInPlace(SimulatedCTF);

                            Score += MathHelper.CrossCorrelate(SubtractedLocal1D[s], SimulatedCTF);
                        }

                        lock (ForPS1D)
                            if (Score > BestScore)
                            {
                                BestScore = Score;
                                BestZ = z;
                                BestPhase = p;
                            }
                    }
                });

                GlobalCTF = new CTF
                {
                    PixelSize = options.BinnedPixelSizeMean,

                    Defocus = (decimal)BestZ,
                    PhaseShift = (decimal)BestPhase,

                    Cs = options.Cs,
                    Voltage = options.Voltage,
                    Amplitude = options.Amplitude
                };

                //UpdateRotationalAverage(true);  // This doesn't have a nice background yet.

                // Scale everything to one common defocus value
                {
                    CTFStruct[] LocalParams = GridDeltas.Select((v, i) =>
                    {
                        CTF Local = GlobalCTF.GetCopy();
                        Local.Defocus += (decimal)v;
                        Local.Scale = (decimal)Math.Pow(1 - Math.Abs(Math.Sin(Angles[i / NPositions] * Helper.ToRad)), 2);

                        return Local.ToStruct();
                    }).ToArray();

                    Image CTFAverage1D = new Image(IntPtr.Zero, new int3(DimsRegion.X / 2, 1, 1));
                    CTF CTFAug = GlobalCTF.GetCopy();

                    GPU.CTFMakeAverage(CTFSpectra.GetDevice(Intent.Read),
                                       CTFCoordsCart.GetDevice(Intent.Read),
                                       (uint)CTFSpectra.ElementsSliceReal,
                                       (uint)DimsRegion.X,
                                       LocalParams,
                                       CTFAug.ToStruct(),
                                       0,
                                       (uint)DimsRegion.X / 2,
                                       (uint)LocalParams.Length,
                                       CTFAverage1D.GetDevice(Intent.Write));

                    float[] RotationalAverageData = CTFAverage1D.GetHost(Intent.Read)[0];
                    for (int i = 0; i < RotationalAverageData.Length; i++)
                        GlobalPS1D[i] = new float2((float)i / DimsRegion.X, RotationalAverageData[i]);
                    MathHelper.UnNaN(GlobalPS1D);

                    CTFAverage1D.Dispose();
                    CTFSpectra.FreeDevice();
                }

                UpdateBackgroundFit();          // Now get a reasonably nice background.

                #region For debug purposes, check what the background-subtracted average looks like at this point

                // Scale everything to one common defocus value
                if (false)
                {
                    Image CTFSpectraBackground = new Image(new int3(DimsRegion), true);
                    float[] CTFSpectraBackgroundData = CTFSpectraBackground.GetHost(Intent.Write)[0];

                    // Construct background in Cartesian coordinates.
                    Helper.ForEachElementFT(DimsRegion, (x, y, xx, yy, r, a) =>
                    {
                        CTFSpectraBackgroundData[y * CTFSpectraBackground.DimsEffective.X + x] = GlobalBackground.Interp(r / DimsRegion.X);
                    });

                    CTFSpectra.SubtractFromSlices(CTFSpectraBackground);

                    CTFStruct[] LocalParams = GridDeltas.Select(v =>
                    {
                        CTF Local = GlobalCTF.GetCopy();
                        Local.Defocus += (decimal)v;

                        return Local.ToStruct();
                    }).ToArray();

                    Image CTFAverage1D = new Image(IntPtr.Zero, new int3(DimsRegion.X / 2, 1, 1));
                    CTF CTFAug = GlobalCTF.GetCopy();

                    GPU.CTFMakeAverage(CTFSpectra.GetDevice(Intent.Read),
                                       CTFCoordsCart.GetDevice(Intent.Read),
                                       (uint)CTFSpectra.ElementsSliceReal,
                                       (uint)DimsRegion.X,
                                       LocalParams,
                                       CTFAug.ToStruct(),
                                       0,
                                       (uint)DimsRegion.X / 2,
                                       (uint)LocalParams.Length,
                                       CTFAverage1D.GetDevice(Intent.Write));

                    float[] RotationalAverageData = CTFAverage1D.GetHost(Intent.Read)[0];
                    for (int i = 0; i < RotationalAverageData.Length; i++)
                        GlobalPS1D[i] = new float2((float)i / DimsRegion.X, RotationalAverageData[i]);
                    MathHelper.UnNaN(GlobalPS1D);

                    CTFSpectra.AddToSlices(CTFSpectraBackground);

                    CTFSpectraBackground.Dispose();
                    CTFAverage1D.Dispose();
                    CTFSpectra.FreeDevice();
                }

                #endregion
            }
            #endregion

            // Do BFGS optimization of defocus, astigmatism and phase shift,
            // using 2D simulation for comparison

            double[] StartParams = new double[5];
            StartParams[0] = 0;
            StartParams[1] = 0;
            StartParams[2] = (double)GlobalCTF.DefocusDelta;
            StartParams[3] = (double)GlobalCTF.DefocusDelta;
            StartParams[4] = (double)GlobalCTF.DefocusAngle / 20 * Helper.ToRad;
            StartParams = Helper.Combine(StartParams,
                                         Helper.ArrayOfConstant((double)GlobalCTF.Defocus, NFrames),
                                         Helper.ArrayOfConstant((double)GlobalCTF.PhaseShift, Math.Max(1, NFrames / 3)));

            float3[] GridCoordsByAngle = Helper.ArrayOfFunction(i => new float3((float)i / (NFrames - 1), 0, 0), NFrames);
            float3[] GridCoordsByDose = Helper.ArrayOfFunction(i => new float3(Dose[i] / MathHelper.Max(Dose), 0, 0), NFrames);

            #region BFGS

            {
                // Second iteration will have a nicer background
                for (int opt = 0; opt < 1; opt++)
                {
                    if (opt > 0)
                        NFreq = Math.Min(NFreq + 10, DimsRegion.X / 2 - MinFreqInclusive - 1);

                    Image CTFSpectraPolarTrimmed = CTFSpectra.AsPolar((uint)MinFreqInclusive, (uint)(MinFreqInclusive + NFreq));
                    CTFSpectra.FreeDevice(); // This will only be needed again for the final PS1D.

                    #region Create background and scale

                    float[] CurrentScale = Helper.ArrayOfConstant(1f, GlobalPS1D.Length);// GlobalScale.Interp(GlobalPS1D.Select(p => p.X).ToArray());

                    Image CTFSpectraScale = new Image(new int3(NFreq, DimsRegion.X, 1));
                    float[] CTFSpectraScaleData = CTFSpectraScale.GetHost(Intent.Write)[0];

                    // Trim polar to relevant frequencies, and populate coordinates.
                    Parallel.For(0, DimsRegion.X, y =>
                    {
                        for (int x = 0; x < NFreq; x++)
                            CTFSpectraScaleData[y * NFreq + x] = CurrentScale[x + MinFreqInclusive];
                    });
                    //CTFSpectraScale.WriteMRC("ctfspectrascale.mrc");

                    // Background is just 1 line since we're in polar.
                    Image CurrentBackground = new Image(GlobalBackground.Interp(GlobalPS1D.Select(p => p.X).ToArray()).Skip(MinFreqInclusive).Take(NFreq).ToArray());

                    CTFSpectraPolarTrimmed.SubtractFromLines(CurrentBackground);
                    CurrentBackground.Dispose();

                    //CTFSpectraPolarTrimmed.WriteMRC("ctfspectrapolartrimmed.mrc");

                    Image IceMask = new Image(CTFSpectraScale.Dims);   // Not doing ice ring modeling in tomo

                    #endregion

                    #region Eval and Gradient methods

                    // Helper method for getting CTFStructs for the entire spectra grid.
                    Func<double[], CTF, float[], float[], float[], CTFStruct[]> EvalGetCTF = (input, ctf, phaseValues, defocusValues, defocusDeltaValues) =>
                    {
                        CTF Local = ctf.GetCopy();
                        Local.DefocusAngle = (decimal)(input[4] * 20 / (Math.PI / 180));

                        CTFStruct LocalStruct = Local.ToStruct();
                        CTFStruct[] LocalParams = new CTFStruct[defocusValues.Length];
                        for (int f = 0; f < NFrames; f++)
                            for (int p = 0; p < NPositions; p++)
                            {
                                LocalParams[f * NPositions + p] = LocalStruct;
                                LocalParams[f * NPositions + p].Defocus = defocusValues[f * NPositions + p] * -1e-6f;
                                LocalParams[f * NPositions + p].DefocusDelta = defocusDeltaValues[f] * -1e-6f;
                                LocalParams[f * NPositions + p].PhaseShift = phaseValues[f] * (float)Math.PI;
                            }

                        return LocalParams;
                    };

                    Func<double[], double> Eval = input =>
                    {
                        GlobalPlaneAngle = new float2((float)input[0], (float)input[1]) * Helper.ToDeg;

                        CubicGrid TempGridPhase = new CubicGrid(new int3(Math.Max(1, NFrames / 3), 1, 1), input.Skip(5 + NFrames).Take(Math.Max(1, NFrames / 3)).Select(v => (float)v).ToArray());
                        CubicGrid TempGridDefocus = new CubicGrid(new int3(NFrames, 1, 1), input.Skip(5).Take(NFrames).Select(v => (float)v).ToArray());
                        CubicGrid TempGridDefocusDelta = new CubicGrid(new int3(1, 1, 1), new[] { (float)input[2] });

                        float[] PhaseValues = TempGridPhase.GetInterpolated(GridCoordsByDose);
                        float[] DefocusValues = GetDefocusGrid(TempGridDefocus.GetInterpolated(GridCoordsByAngle));
                        float[] DefocusDeltaValues = TempGridDefocusDelta.GetInterpolated(GridCoordsByDose);

                        CTFStruct[] LocalParams = EvalGetCTF(input, GlobalCTF, PhaseValues, DefocusValues, DefocusDeltaValues);

                        float[] Result = new float[LocalParams.Length];

                        GPU.CTFCompareToSim(CTFSpectraPolarTrimmed.GetDevice(Intent.Read),
                                            CTFCoordsPolarTrimmed.GetDevice(Intent.Read),
                                            CTFSpectraScale.GetDevice(Intent.Read),
                                            IceMask.GetDevice(Intent.Read),
                                            0,
                                            (uint)CTFSpectraPolarTrimmed.ElementsSliceReal,
                                            LocalParams,
                                            Result,
                                            (uint)LocalParams.Length);

                        float Score = Result.Sum();

                        if (float.IsNaN(Score) || float.IsInfinity(Score))
                            throw new Exception("Bad score.");

                        return Score;
                    };

                    Func<double[], double[]> Gradient = input =>
                    {
                        const float Step = 0.0025f;
                        double[] Result = new double[input.Length];

                        for (int i = 0; i < input.Length; i++)
                        {
                            if (!options.DoPhase && i >= 5 + NFrames)
                                continue;

                            double[] UpperInput = new double[input.Length];
                            input.CopyTo(UpperInput, 0);
                            UpperInput[i] += Step;
                            double UpperValue = Eval(UpperInput);

                            double[] LowerInput = new double[input.Length];
                            input.CopyTo(LowerInput, 0);
                            LowerInput[i] -= Step;
                            double LowerValue = Eval(LowerInput);

                            Result[i] = (UpperValue - LowerValue) / (2f * Step);
                        }

                        if (Result.Any(i => double.IsNaN(i) || double.IsInfinity(i)))
                            throw new Exception("Bad score.");

                        return Result;
                    };

                    #endregion

                    #region Do optimization

                    // StartParams are initialized above, before the optimization loop

                    BroydenFletcherGoldfarbShanno Optimizer = new BroydenFletcherGoldfarbShanno(StartParams.Length, Eval, Gradient)
                    {
                        MaxIterations = 15
                    };
                    Optimizer.Maximize(StartParams);

                    #endregion

                    #region Retrieve parameters

                    GlobalCTF.Defocus = (decimal)MathHelper.Mean(Optimizer.Solution.Skip(5).Take(NFrames).Select(v => (float)v));
                    GlobalCTF.PhaseShift = (decimal)MathHelper.Mean(Optimizer.Solution.Skip(5 + NFrames).Take(Math.Max(1, NFrames / 3)).Select(v => (float)v));
                    GlobalCTF.DefocusDelta = (decimal)(Optimizer.Solution[2]) / 1;
                    GlobalCTF.DefocusAngle = (decimal)(Optimizer.Solution[4] * 20 * Helper.ToDeg);

                    if (GlobalCTF.DefocusDelta < 0)
                    {
                        GlobalCTF.DefocusAngle += 90;
                        GlobalCTF.DefocusDelta *= -1;
                    }
                    GlobalCTF.DefocusAngle = ((int)GlobalCTF.DefocusAngle + 180 * 99) % 180;

                    GlobalPlaneAngle = new float2((float)Optimizer.Solution[0],
                                                  (float)Optimizer.Solution[1]) * Helper.ToDeg;

                    {
                        CubicGrid TempGridPhase = new CubicGrid(new int3(Math.Max(1, NFrames / 3), 1, 1), StartParams.Skip(5 + NFrames).Take(Math.Max(1, NFrames / 3)).Select(v => (float)v).ToArray());
                        CubicGrid TempGridDefocusDelta = new CubicGrid(new int3(1, 1, 1), new[] { (float)GlobalCTF.DefocusDelta });
                        CubicGrid TempGridDefocus = new CubicGrid(new int3(NFrames, 1, 1), StartParams.Skip(5).Take(NFrames).Select(v => (float)v).ToArray());

                        GridCTFDefocus = new CubicGrid(new int3(1, 1, NTilts), TempGridDefocus.GetInterpolated(GridCoordsByAngle));
                        GridCTFDefocusDelta = new CubicGrid(new int3(1, 1, NTilts), TempGridDefocusDelta.GetInterpolated(GridCoordsByDose));
                        GridCTFDefocusAngle = new CubicGrid(new int3(1, 1, NTilts), Helper.ArrayOfConstant((float)GlobalCTF.DefocusAngle, NTilts));
                        GridCTFPhase = new CubicGrid(new int3(1, 1, NTilts), TempGridPhase.GetInterpolated(GridCoordsByDose));
                    }

                    #endregion

                    // Dispose GPU resources manually because GC can't be bothered to do it in time.
                    CTFSpectraPolarTrimmed.Dispose();
                    CTFSpectraScale.Dispose();
                    IceMask.Dispose();

                    #region Get nicer envelope fit

                    // Scale everything to one common defocus value
                    {
                        float3[] GridCoords = Helper.ArrayOfFunction(i => new float3(0, 0, (float)i / (NFrames - 1)), NFrames);

                        float[] DefocusValues = GetDefocusGrid(GridCTFDefocus.GetInterpolated(GridCoords));
                        float[] DefocusDeltaValues = GridCTFDefocusDelta.GetInterpolated(GridCoords);
                        float[] DefocusAngleValues = GridCTFDefocusAngle.GetInterpolated(GridCoords);
                        float[] PhaseValues = GridCTFPhase.GetInterpolated(GridCoords);

                        CTFStruct[] LocalParams = new CTFStruct[DefocusValues.Length];
                        for (int f = 0; f < NFrames; f++)
                        {
                            for (int p = 0; p < NPositions; p++)
                            {
                                CTF Local = GlobalCTF.GetCopy();
                                Local.Defocus = (decimal)DefocusValues[f * NPositions + p];
                                Local.DefocusDelta = (decimal)DefocusDeltaValues[f];
                                Local.DefocusAngle = (decimal)DefocusAngleValues[f];
                                Local.PhaseShift = (decimal)PhaseValues[f];

                                LocalParams[f * NPositions + p] = Local.ToStruct();
                            }
                        }

                        Image CTFAverage1D = new Image(IntPtr.Zero, new int3(DimsRegion.X / 2, 1, 1));
                        CTF CTFAug = GlobalCTF.GetCopy();

                        GPU.CTFMakeAverage(CTFSpectra.GetDevice(Intent.Read),
                                            CTFCoordsCart.GetDevice(Intent.Read),
                                            (uint)CTFSpectra.ElementsSliceReal,
                                            (uint)DimsRegion.X,
                                            LocalParams,
                                            CTFAug.ToStruct(),
                                            0,
                                            (uint)DimsRegion.X / 2,
                                            (uint)LocalParams.Length,
                                            CTFAverage1D.GetDevice(Intent.Write));

                        float[] RotationalAverageData = CTFAverage1D.GetHost(Intent.Read)[0];
                        for (int i = 0; i < RotationalAverageData.Length; i++)
                            GlobalPS1D[i] = new float2((float)i / DimsRegion.X, RotationalAverageData[i]);
                        MathHelper.UnNaN(GlobalPS1D);

                        CTFAverage1D.Dispose();
                        CTFSpectra.FreeDevice();

                        UpdateBackgroundFit(); // Now get a nice background.
                    }

                    #endregion
                }
            }

            #endregion

            #region Create global, and per-tilt average spectra

            {
                TiltPS1D = new ObservableCollection<float2[]>();
                TiltSimulatedBackground = new ObservableCollection<Cubic1D>();
                TiltSimulatedScale = new ObservableCollection<Cubic1D>();
                Image AllPS2D = new Image(new int3(DimsRegion.X, DimsRegion.X / 2, NTilts));

                float3[] GridCoords = Helper.ArrayOfFunction(i => new float3(0, 0, (float)i / (NFrames - 1)), NFrames);

                float[] DefocusValues = GetDefocusGrid(GridCTFDefocus.GetInterpolated(GridCoords));
                float[] DefocusDeltaValues = GridCTFDefocusDelta.GetInterpolated(GridCoords);
                float[] DefocusAngleValues = GridCTFDefocusAngle.GetInterpolated(GridCoords);
                float[] PhaseValues = GridCTFPhase.GetInterpolated(GridCoords);

                // Scale everything to one common defocus value
                {
                    Image CTFSpectraBackground = new Image(new int3(DimsRegion), true);
                    float[] CTFSpectraBackgroundData = CTFSpectraBackground.GetHost(Intent.Write)[0];

                    // Construct background in Cartesian coordinates.
                    Helper.ForEachElementFT(DimsRegion, (x, y, xx, yy, r, a) =>
                    {
                        CTFSpectraBackgroundData[y * CTFSpectraBackground.DimsEffective.X + x] = GlobalBackground.Interp(r / DimsRegion.X);
                    });

                    CTFSpectra.SubtractFromSlices(CTFSpectraBackground);

                    CTFStruct[] LocalParams = new CTFStruct[DefocusValues.Length];
                    for (int f = 0; f < NFrames; f++)
                    {
                        for (int p = 0; p < NPositions; p++)
                        {
                            CTF Local = GlobalCTF.GetCopy();
                            Local.Defocus = (decimal)DefocusValues[f * NPositions + p];
                            Local.DefocusDelta = (decimal)DefocusDeltaValues[f];
                            Local.DefocusAngle = (decimal)DefocusAngleValues[f];
                            Local.PhaseShift = (decimal)PhaseValues[f];
                            Local.Scale = (decimal)Math.Pow(1 - Math.Abs(Math.Sin(Angles[f] * Helper.ToRad)), 2);

                            LocalParams[f * NPositions + p] = Local.ToStruct();
                        }
                    }

                    Image CTFAverage1D = new Image(IntPtr.Zero, new int3(DimsRegion.X / 2, 1, 1));
                    CTF CTFAug = GlobalCTF.GetCopy();

                    {
                        GPU.CTFMakeAverage(CTFSpectra.GetDevice(Intent.Read),
                                           CTFCoordsCart.GetDevice(Intent.Read),
                                           (uint)CTFSpectra.ElementsSliceReal,
                                           (uint)DimsRegion.X,
                                           LocalParams,
                                           CTFAug.ToStruct(),
                                           0,
                                           (uint)DimsRegion.X / 2,
                                           (uint)LocalParams.Length,
                                           CTFAverage1D.GetDevice(Intent.Write));

                        float[] RotationalAverageData = CTFAverage1D.GetHost(Intent.Read)[0];
                        for (int i = 0; i < RotationalAverageData.Length; i++)
                            GlobalPS1D[i] = new float2((float)i / DimsRegion.X, RotationalAverageData[i]);
                        MathHelper.UnNaN(GlobalPS1D);

                        PS1D = GlobalPS1D.ToArray();
                    }

                    #region Now go through all tilts

                    for (int t = 0; t < NTilts; t++)
                    {
                        CTFAug.Defocus = (decimal)GridCTFDefocus.FlatValues[t];

                        GPU.CTFMakeAverage(CTFSpectra.GetDeviceSlice(t * NPositions, Intent.Read),
                                           CTFCoordsCart.GetDevice(Intent.Read),
                                           (uint)CTFSpectra.ElementsSliceReal,
                                           (uint)DimsRegion.X,
                                           LocalParams.Skip(t * NPositions).Take(NPositions).ToArray(),
                                           CTFAug.ToStruct(),
                                           0,
                                           (uint)DimsRegion.X / 2,
                                           (uint)NPositions,
                                           CTFAverage1D.GetDevice(Intent.Write));

                        float[] RotationalAverageData = CTFAverage1D.GetHost(Intent.Read)[0];
                        for (int i = 0; i < RotationalAverageData.Length; i++)
                            GlobalPS1D[i] = new float2((float)i / DimsRegion.X, RotationalAverageData[i]);
                        MathHelper.UnNaN(GlobalPS1D);

                        TiltPS1D.Add(GlobalPS1D.ToArray());
                        TiltSimulatedBackground.Add(new Cubic1D(GlobalBackground.Data.ToArray()));
                        TiltSimulatedScale.Add(new Cubic1D(GlobalScale.Data.ToArray()));

                        #region Make 2D power spectrum for display

                        Image Sum2D = new Image(CTFSpectra.Dims.Slice(), true);
                        GPU.ReduceMean(CTFSpectra.GetDeviceSlice(t * NPositions, Intent.Read),
                                       Sum2D.GetDevice(Intent.Write),
                                       (uint)Sum2D.ElementsReal,
                                       (uint)NPositions,
                                       1);

                        float[] Sum2DData = Sum2D.GetHostContinuousCopy();
                        Sum2D.Dispose();

                        int3 DimsAverage = new int3(DimsRegion.X, DimsRegion.X / 2, 1);
                        float[] Average2DData = new float[DimsAverage.Elements()];
                        int DimHalf = DimsRegion.X / 2;

                        for (int y = 0; y < DimsAverage.Y; y++)
                        {
                            int yy = y * y;
                            for (int x = 0; x < DimHalf; x++)
                            {
                                int xx = x;
                                xx *= xx;
                                float r = (float)Math.Sqrt(xx + yy) / DimsRegion.X;
                                Average2DData[(DimsAverage.Y - 1 - y) * DimsAverage.X + x + DimHalf] = Sum2DData[(DimsRegion.X - 1 - y) * (DimsRegion.X / 2 + 1) + x];
                            }

                            for (int x = 1; x < DimHalf; x++)
                            {
                                int xx = -(x - DimHalf);
                                float r = (float)Math.Sqrt(xx * xx + yy) / DimsRegion.X;
                                Average2DData[(DimsAverage.Y - 1 - y) * DimsAverage.X + x] = Sum2DData[y * (DimsRegion.X / 2 + 1) + xx];
                            }
                        }

                        AllPS2D.GetHost(Intent.Write)[t] = Average2DData;

                        #endregion
                    }

                    #endregion

                    AllPS2D.WriteMRC(PowerSpectrumPath, true);

                    CTFSpectraBackground.Dispose();
                    CTFAverage1D.Dispose();
                    CTFSpectra.FreeDevice();
                }
            }

            #endregion

            CTF = GlobalCTF;
            SimulatedScale = GlobalScale;
            PlaneNormal = (Matrix3.RotateX(GlobalPlaneAngle.X * Helper.ToRad) * Matrix3.RotateY(GlobalPlaneAngle.Y * Helper.ToRad)) * new float3(0, 0, 1);

            #region Estimate fittable resolution

            {
                float[] Quality = CTF.EstimateQuality(PS1D.Select(p => p.Y).ToArray(),
                                                      SimulatedScale.Interp(PS1D.Select(p => p.X).ToArray()),
                                                      (float)options.RangeMin, 16, true);
                int FirstFreq = 0;
                while ((float.IsNaN(Quality[FirstFreq]) || Quality[FirstFreq] < 0.8f) && FirstFreq < Quality.Length - 1)
                    FirstFreq++;

                int LastFreq = FirstFreq;
                while (!float.IsNaN(Quality[LastFreq]) && Quality[LastFreq] > 0.3f && LastFreq < Quality.Length - 1)
                    LastFreq++;

                CTFResolutionEstimate = Math.Round(options.BinnedPixelSizeMean / ((decimal)LastFreq / options.Window), 1);
            }

            #endregion

            CTFSpectra.Dispose();
            CTFMean.Dispose();
            CTFCoordsCart.Dispose();
            CTFCoordsPolarTrimmed.Dispose();

            Simulated1D = GetSimulated1D();

            OptionsCTF = options;

            SaveMeta();

            IsProcessing = false;
            TiltCTFProcessed?.Invoke();
        }

        #endregion

        public void MatchFull(ProcessingOptionsTomoFullMatch options, Image template, Func<int3, int, string, bool> progressCallback)
        {
            bool IsCanceled = false;
            if (!Directory.Exists(MatchingDir))
                Directory.CreateDirectory(MatchingDir);

            string NameWithRes = RootName + $"_{options.BinnedPixelSizeMean:F2}Apx";

            float3[] HealpixAngles = Helper.GetHealpixAngles(options.HealpixOrder, options.Symmetry).Select(a => a * Helper.ToRad).ToArray();
            LoadMovieSizes(options);

            Image CorrImage = null;
            float[][] CorrData;
            float[][] AngleData;

            #region Dimensions

            int SizeSub = options.SubVolumeSize;
            int SizeParticle = (int)(options.TemplateDiameter / options.BinnedPixelSizeMean);
            int SizeUseful = Math.Min(SizeSub / 2, SizeSub - SizeParticle * 2);// Math.Min(SizeSub - SizeParticle, SizeSub / 2);
            if (SizeUseful < 2)
                throw new DimensionMismatchException("Particle diameter is bigger than the box.");

            VolumeDimensionsPhysical = options.DimensionsPhysical;

            int3 DimsVolumeCropped = new int3((int)Math.Round(options.DimensionsPhysical.X / (float)options.BinnedPixelSizeMean / 2) * 2,
                                                (int)Math.Round(options.DimensionsPhysical.Y / (float)options.BinnedPixelSizeMean / 2) * 2,
                                                (int)Math.Round(options.DimensionsPhysical.Z / (float)options.BinnedPixelSizeMean / 2) * 2);

            int3 Grid = (DimsVolumeCropped - SizeParticle + SizeUseful - 1) / SizeUseful;
            List<float3> GridCoords = new List<float3>();
            for (int z = 0; z < Grid.Z; z++)
                for (int x = 0; x < Grid.X; x++)
                    for (int y = 0; y < Grid.Y; y++)
                        GridCoords.Add(new float3(x * SizeUseful + SizeUseful / 2 + SizeParticle / 2,
                                                    y * SizeUseful + SizeUseful / 2 + SizeParticle / 2,
                                                    z * SizeUseful + SizeUseful / 2 + SizeParticle / 2));

            #endregion

            #region Get correlation and angles either by calculating them from scratch, or by loading precalculated volumes

            string CorrVolumePath = MatchingDir + NameWithRes + "_" + options.TemplateName + "_corr.mrc";

            CorrData = Helper.ArrayOfFunction(i => new float[DimsVolumeCropped.ElementsSlice()], DimsVolumeCropped.Z);
            AngleData = Helper.ArrayOfFunction(i => new float[DimsVolumeCropped.ElementsSlice()], DimsVolumeCropped.Z);

            if (!File.Exists(CorrVolumePath) || !options.ReuseCorrVolumes)
            {
                if (!File.Exists(ReconstructionDir + NameWithRes + ".mrc"))
                    return;
                //throw new FileNotFoundException("A reconstruction at the desired resolution was not found.");

                progressCallback?.Invoke(Grid, 0, "Loading...");

                Image TomoRec = Image.FromFile(ReconstructionDir + NameWithRes + ".mrc");

                #region Scale and pad/crop the template to the right size, create projector

                progressCallback?.Invoke(Grid, 0, "Preparing template...");

                Projector ProjectorReference;
                {
                    int SizeBinned = (int)Math.Round(template.Dims.X * (options.TemplatePixel / options.BinnedPixelSizeMean) / 2) * 2;

                    Image TemplateScaled = template.AsScaled(new int3(SizeBinned));
                    template.FreeDevice();

                    GPU.SphereMask(TemplateScaled.GetDevice(Intent.Read),
                                   TemplateScaled.GetDevice(Intent.Write),
                                   TemplateScaled.Dims,
                                   SizeParticle / 2,
                                   Math.Max(5, 20 / (float)options.BinnedPixelSizeMean),
                                   false,
                                   1);

                    Image TemplatePadded = TemplateScaled.AsPadded(new int3(SizeSub));
                    TemplateScaled.Dispose();

                    ProjectorReference = new Projector(TemplatePadded, 2, 3);
                    TemplatePadded.Dispose();
                    ProjectorReference.PutTexturesOnDevice();
                }

                #endregion

                #region Preflight

                if (TomoRec.Dims != DimsVolumeCropped)
                    throw new DimensionMismatchException("Reconstruction resolution doesn't match desired correlation resolution.");

                if (options.WhitenSpectrum)
                {
                    progressCallback?.Invoke(Grid, 0, "Whitening spectral noise...");

                    Image TomoRecFlat = TomoRec.AsSpectrumFlattened(true, 0.99f);
                    TomoRec.Dispose();
                    TomoRec = TomoRecFlat;
                }

                float[][] TomoRecData = TomoRec.GetHost(Intent.Read);

                int PlanForw, PlanBack, PlanForwCTF;
                Projector.GetPlans(new int3(SizeSub), 3, out PlanForw, out PlanBack, out PlanForwCTF);

                Image CTFCoords = CTF.GetCTFCoords(SizeSub, SizeSub);

                #endregion

                progressCallback?.Invoke(Grid, 0, "Matching...");

                int BatchSize = Grid.Y;
                for (int b = 0; b < GridCoords.Count; b += BatchSize)
                {
                    int CurBatch = Math.Min(BatchSize, GridCoords.Count - b);

                    Image CorrSum = new Image(new int3(SizeSub));
                    Image Subtomos = new Image(IntPtr.Zero, new int3(SizeSub, SizeSub, SizeSub * CurBatch), true, true);

                    #region Create CTF for this column of subvolumes (X = const, Z = const)

                    Image SubtomoCTF;
                    Image CTFs = GetCTFsForOneParticle(options, GridCoords[b], CTFCoords, null, true, false, false);
                    //CTFs.Fill(1);
                    Image CTFsAbs = CTFs.GetCopyGPU();
                    CTFsAbs.Abs();
                    {
                        // CTF has to be converted to complex numbers with imag = 0, and weighted by itself
                        float2[] CTFsComplexData = new float2[CTFs.ElementsComplex];
                        float[] CTFsContinuousData = CTFs.GetHostContinuousCopy();
                        for (int i = 0; i < CTFsComplexData.Length; i++)
                            CTFsComplexData[i] = new float2(CTFsContinuousData[i] * CTFsContinuousData[i], 0);
                        //CTFsAbs.Fill(1f);

                        Image CTFsComplex = new Image(CTFsComplexData, CTFs.Dims, true);

                        // Back-project and reconstruct
                        Projector ProjCTF = new Projector(new int3(SizeSub), 3);

                        ProjCTF.Weights.Fill(0.01f);

                        ProjCTF.BackProject(CTFsComplex, CTFsAbs, GetAngleInAllTilts(GridCoords[b]), MagnificationCorrection);
                        CTFsComplex.Dispose();

                        SubtomoCTF = ProjCTF.Reconstruct(true, "C1", PlanForw, PlanBack, PlanForwCTF);
                        ProjCTF.Dispose();

                        //Image SubtomoFT = new Image(new int3(SizeSub), true, true);
                        //GPU.BackProjectTomo(SubtomoFT.GetDevice(Intent.Write),
                        //                    SubtomoFT.Dims,
                        //                    CTFsComplex.GetDevice(Intent.Read),
                        //                    CTFsAbs.GetDevice(Intent.Read),
                        //                    CTFsComplex.Dims.Slice(),
                        //                    (uint)SizeSub / 2,
                        //                    Helper.ToInterleaved(GetAngleInAllTilts(GridCoords[b])),
                        //                    (uint)CTFsComplex.Dims.Z);
                        //SubtomoCTF = SubtomoFT.AsReal();
                        //SubtomoFT.Dispose();
                        //CTFsComplex.Dispose();
                    }
                    //SubtomoCTF.Fill(1f);
                    //SubtomoCTF.WriteMRC("d_ctf.mrc", true);

                    CTFs.Dispose();
                    CTFsAbs.Dispose();

                    #endregion

                    #region Extract subvolumes and store their FFTs

                    for (int st = 0; st < CurBatch; st++)
                    {
                        float[][] SubtomoData = new float[SizeSub][];

                        int XStart = (int)GridCoords[b + st].X - SizeSub / 2;
                        int YStart = (int)GridCoords[b + st].Y - SizeSub / 2;
                        int ZStart = (int)GridCoords[b + st].Z - SizeSub / 2;
                        for (int z = 0; z < SizeSub; z++)
                        {
                            SubtomoData[z] = new float[SizeSub * SizeSub];
                            int zz = (ZStart + z + TomoRec.Dims.Z) % TomoRec.Dims.Z;

                            for (int y = 0; y < SizeSub; y++)
                            {
                                int yy = (YStart + y + TomoRec.Dims.Y) % TomoRec.Dims.Y;
                                for (int x = 0; x < SizeSub; x++)
                                {
                                    int xx = (XStart + x + TomoRec.Dims.X) % TomoRec.Dims.X;
                                    SubtomoData[z][y * SizeSub + x] = TomoRecData[zz][yy * TomoRec.Dims.X + xx];
                                }
                            }
                        }

                        Image Subtomo = new Image(SubtomoData, new int3(SizeSub));

                        // Re-use FFT plan created previously for CTF reconstruction since it has the right size
                        GPU.FFT(Subtomo.GetDevice(Intent.Read),
                                Subtomos.GetDeviceSlice(SizeSub * st, Intent.Write),
                                Subtomo.Dims,
                                1,
                                PlanForwCTF);

                        Subtomo.Dispose();
                    }

                    #endregion

                    #region Perform correlation

                    Image BestCorrelation = new Image(IntPtr.Zero, new int3(SizeSub, SizeSub, SizeSub * CurBatch));
                    Image BestAngle = new Image(IntPtr.Zero, new int3(SizeSub, SizeSub, SizeSub * CurBatch));

                    GPU.CorrelateSubTomos(ProjectorReference.t_DataRe,
                                          ProjectorReference.t_DataIm,
                                          ProjectorReference.Oversampling,
                                          ProjectorReference.Data.Dims,
                                          Subtomos.GetDevice(Intent.Read),
                                          SubtomoCTF.GetDevice(Intent.Read),
                                          new int3(SizeSub),
                                          (uint)CurBatch,
                                          Helper.ToInterleaved(HealpixAngles),
                                          (uint)HealpixAngles.Length,
                                          SizeParticle / 2,
                                          BestCorrelation.GetDevice(Intent.Write),
                                          BestAngle.GetDevice(Intent.Write));

                    #endregion

                    #region Put correlation values and best angle IDs back into the large volume

                    // Compensate for the fact I'm too stupid to figure out why the real-space result is attenuated in weird ways
                    float[] CorrSumData;
                    //{
                    //    GPU.ReduceAdd(BestCorrelation.GetDevice(Intent.Read),
                    //                  CorrSum.GetDevice(Intent.Write),
                    //                  (uint)(SizeSub * SizeSub * SizeSub),
                    //                  (uint)CurBatch,
                    //                  1);

                    //    CorrSum.Multiply(1f / CurBatch / (SizeSub * SizeSub * SizeSub));
                    //    CorrSum.Bandpass(0, 4f / SizeSub * 2, true);
                    //    Image CorrSumCropped = CorrSum.AsPadded(new int3(SizeUseful));
                    //    CorrSum.Dispose();

                    //    CorrSumData = CorrSumCropped.GetHostContinuousCopy();
                    //    CorrSumCropped.Dispose();
                    //}

                    for (int st = 0; st < CurBatch; st++)
                    {
                        Image ThisCorrelation = new Image(BestCorrelation.GetDeviceSlice(SizeSub * st, Intent.Read), new int3(SizeSub));
                        Image CroppedCorrelation = ThisCorrelation.AsPadded(new int3(SizeUseful));

                        Image ThisAngle = new Image(BestAngle.GetDeviceSlice(SizeSub * st, Intent.Read), new int3(SizeSub));
                        Image CroppedAngle = ThisAngle.AsPadded(new int3(SizeUseful));

                        float[] SubCorr = CroppedCorrelation.GetHostContinuousCopy();
                        float[] SubAngle = CroppedAngle.GetHostContinuousCopy();
                        int3 Origin = new int3(GridCoords[b + st]) - SizeUseful / 2;
                        for (int z = 0; z < SizeUseful; z++)
                        {
                            int zVol = Origin.Z + z;
                            if (zVol >= DimsVolumeCropped.Z - SizeParticle / 2)
                                continue;

                            for (int y = 0; y < SizeUseful; y++)
                            {
                                int yVol = Origin.Y + y;
                                if (yVol >= DimsVolumeCropped.Y - SizeParticle / 2)
                                    continue;

                                for (int x = 0; x < SizeUseful; x++)
                                {
                                    int xVol = Origin.X + x;
                                    if (xVol >= DimsVolumeCropped.X - SizeParticle / 2)
                                        continue;

                                    CorrData[zVol][yVol * DimsVolumeCropped.X + xVol] = SubCorr[(z * SizeUseful + y) * SizeUseful + x];// / (SizeSub * SizeSub * SizeSub);// / CorrSumData[(z * SizeUseful + y) * SizeUseful + x];
                                    AngleData[zVol][yVol * DimsVolumeCropped.X + xVol] = SubAngle[(z * SizeUseful + y) * SizeUseful + x];
                                }
                            }
                        }

                        CroppedCorrelation.Dispose();
                        ThisCorrelation.Dispose();
                        CroppedAngle.Dispose();
                        ThisAngle.Dispose();
                    }

                    #endregion

                    Subtomos.Dispose();
                    SubtomoCTF.Dispose();

                    BestCorrelation.Dispose();
                    BestAngle.Dispose();

                    if (progressCallback != null)
                        IsCanceled = progressCallback(Grid, b + CurBatch, "Matching...");
                }

                #region Postflight

                TomoRec.Dispose();

                GPU.DestroyFFTPlan(PlanForw);
                GPU.DestroyFFTPlan(PlanBack);
                GPU.DestroyFFTPlan(PlanForwCTF);

                CTFCoords.Dispose();
                ProjectorReference.Dispose();

                if (options.Supersample > 1)
                {
                    progressCallback?.Invoke(Grid, (int)Grid.Elements(), "Looking for sub-pixel peaks...");

                    Image NormalSampled = new Image(CorrData, DimsVolumeCropped);
                    Image SuperSampled = new Image(NormalSampled.GetDevice(Intent.Read), NormalSampled.Dims);

                    GPU.SubpixelMax(NormalSampled.GetDevice(Intent.Read),
                                    SuperSampled.GetDevice(Intent.Write),
                                    NormalSampled.Dims,
                                    options.Supersample);

                    CorrData = SuperSampled.GetHost(Intent.Read);

                    NormalSampled.Dispose();
                    SuperSampled.Dispose();
                }

                if (options.KeepOnlyFullVoxels)
                {
                    progressCallback?.Invoke(Grid, (int)Grid.Elements(), "Trimming...");

                    float BinnedAngPix = (float)options.BinnedPixelSizeMean;
                    float Margin = (float)options.TemplateDiameter;

                    Parallel.For(0, DimsVolumeCropped.Z, z =>
                    {
                        float3[] VolumePositions = new float3[DimsVolumeCropped.ElementsSlice()];
                        for (int y = 0; y < DimsVolumeCropped.Y; y++)
                            for (int x = 0; x < DimsVolumeCropped.X; x++)
                                VolumePositions[y * DimsVolumeCropped.X + x] = new float3(x * BinnedAngPix, y * BinnedAngPix, z * BinnedAngPix);

                        float3[] ImagePositions = GetPositionInAllTiltsNoLocalWarp(VolumePositions);

                        for (int i = 0; i < ImagePositions.Length; i++)
                        {
                            int ii = i / NTilts;
                            int t = i % NTilts;

                            if (ImagePositions[i].X < Margin || ImagePositions[i].Y < Margin ||
                                ImagePositions[i].X > ImageDimensionsPhysical.X - BinnedAngPix - Margin ||
                                ImagePositions[i].Y > ImageDimensionsPhysical.Y - BinnedAngPix - Margin)
                            {
                                CorrData[z][ii] = 0;
                            }
                        }
                    });
                }

                progressCallback?.Invoke(Grid, (int)Grid.Elements(), "Saving global scores...");

                // Store correlation values and angle IDs for re-use later
                CorrImage = new Image(CorrData, DimsVolumeCropped);
                CorrImage.WriteMRC(CorrVolumePath, (float)options.BinnedPixelSizeMean, true);

                #endregion
            }
            else
            {
                CorrImage = Image.FromFile(CorrVolumePath);
                CorrData = CorrImage.GetHost(Intent.Read);
            }

            //CorrImage?.Dispose();

            #endregion

            #region Get peak list that has at most NResults values

            progressCallback?.Invoke(Grid, (int)Grid.Elements(), "Extracting best peaks...");

            int3[] InitialPeaks = new int3[0];
            {
                float2 MeanAndStd = MathHelper.MeanAndStdNonZero(CorrImage.GetHostContinuousCopy());

                for (float s = 4; s > 0.5f; s -= 0.05f)
                {
                    float Threshold = MeanAndStd.X + MeanAndStd.Y * s;
                    InitialPeaks = CorrImage.GetLocalPeaks(SizeParticle * 2 / 3, Threshold);

                    if (InitialPeaks.Length >= options.NResults)
                        break;
                }
            }

            CorrImage?.Dispose();

            #endregion

            #region Write peak positions and angles into table

            Star TableOut = new Star(new string[]
            {
                "rlnCoordinateX",
                "rlnCoordinateY",
                "rlnCoordinateZ",
                "rlnAngleRot",
                "rlnAngleTilt",
                "rlnAnglePsi",
                "rlnMicrographName",
                "rlnAutopickFigureOfMerit"
            });

            {
                for (int n = 0; n < InitialPeaks.Length; n++)
                {
                    //float3 Position = RefinedPositions[n] / new float3(DimsVolumeCropped);
                    //float Score = RefinedScores[n];
                    //float3 Angle = RefinedAngles[n] * Helper.ToDeg;

                    float3 Position = new float3(InitialPeaks[n]);
                    float Score = CorrData[(int)Position.Z][(int)Position.Y * DimsVolumeCropped.X + (int)Position.X];
                    float3 Angle = HealpixAngles[(int)AngleData[(int)Position.Z][(int)Position.Y * DimsVolumeCropped.X + (int)Position.X]] * Helper.ToDeg;
                    Position /= new float3(DimsVolumeCropped);

                    TableOut.AddRow(new List<string>()
                    {
                        Position.X.ToString(CultureInfo.InvariantCulture),
                        Position.Y.ToString(CultureInfo.InvariantCulture),
                        Position.Z.ToString(CultureInfo.InvariantCulture),
                        Angle.X.ToString(CultureInfo.InvariantCulture),
                        Angle.Y.ToString(CultureInfo.InvariantCulture),
                        Angle.Z.ToString(CultureInfo.InvariantCulture),
                        RootName + ".mrc",
                        Score.ToString(CultureInfo.InvariantCulture)
                    });
                }
            }

            TableOut.Save(MatchingDir + NameWithRes + "_" + options.TemplateName + ".star");

            progressCallback?.Invoke(Grid, (int)Grid.Elements(), "Done.");

            #endregion
        }

        public void ReconstructFull(ProcessingOptionsTomoFullReconstruction options, Func<int3, int, string, bool> progressCallback)
        {
            int GPUID = GPU.GetDevice();

            bool IsCanceled = false;
            string NameWithRes = RootName + $"_{options.BinnedPixelSizeMean:F2}Apx";

            Directory.CreateDirectory(ReconstructionDir);

            if (options.DoDeconv)
                Directory.CreateDirectory(ReconstructionDeconvDir);

            if (options.PrepareDenoising)
            {
                Directory.CreateDirectory(ReconstructionOddDir);
                Directory.CreateDirectory(ReconstructionEvenDir);
            }

            if (File.Exists(ReconstructionDir + NameWithRes + ".mrc") && !options.OverwriteFiles)
                return;

            #region Dimensions

            VolumeDimensionsPhysical = options.DimensionsPhysical;

            int3 DimsVolumeCropped = new int3((int)Math.Round(options.DimensionsPhysical.X / (float)options.BinnedPixelSizeMean / 2) * 2,
                                              (int)Math.Round(options.DimensionsPhysical.Y / (float)options.BinnedPixelSizeMean / 2) * 2,
                                              (int)Math.Round(options.DimensionsPhysical.Z / (float)options.BinnedPixelSizeMean / 2) * 2);
            int SizeSub = options.SubVolumeSize;
            int SizeSubPadded = (int)(SizeSub * options.SubVolumePadding);

            #endregion

            #region Establish reconstruction positions

            int3 Grid = (DimsVolumeCropped + SizeSub - 1) / SizeSub;
            List<float3> GridCoords = new List<float3>();
            for (int z = 0; z < Grid.Z; z++)
                for (int y = 0; y < Grid.Y; y++)
                    for (int x = 0; x < Grid.X; x++)
                        GridCoords.Add(new float3(x * SizeSub + SizeSub / 2,
                                                  y * SizeSub + SizeSub / 2,
                                                  z * SizeSub + SizeSub / 2));

            // progressCallback?.Invoke(Grid, 0, "Loading...");

            #endregion

            #region Load and preprocess tilt series

            Movie[] TiltMovies;
            Image[] TiltData;
            Image[] TiltMasks;
            LoadMovieData(options, true, out TiltMovies, out TiltData);
            LoadMovieMasks(options, out TiltMasks);

            Console.WriteLine("-LoadMovieData, LoadMovieMasks was done.");
            for (int z = 0; z < NTilts; z++)
            {
                EraseDirt(TiltData[z], TiltMasks[z]);
                TiltMasks[z]?.FreeDevice();

                if (options.Normalize)
                {
                    TiltData[z].SubtractMeanGrid(new int2(1));
                    TiltData[z].Bandpass(1f / SizeSub, 1f, false, 0f);

                    GPU.Normalize(TiltData[z].GetDevice(Intent.Read),
                                  TiltData[z].GetDevice(Intent.Write),
                                  (uint)TiltData[z].ElementsReal,
                                  1);
                }

                if (options.Invert)
                    TiltData[z].Multiply(-1f);

                //TiltData[z].Multiply(TiltMasks[z]);
                TiltData[z].FreeDevice();
            }

            #endregion

            #region Memory and FFT plan allocation

            Image CTFCoords = CTF.GetCTFCoords(SizeSubPadded, SizeSubPadded);

            float[][] OutputRec = Helper.ArrayOfFunction(i => new float[DimsVolumeCropped.ElementsSlice()], DimsVolumeCropped.Z);
            float[][] OutputRecDeconv = null;
            float[][][] OutputRecHalves = null;
            if (options.PrepareDenoising)
            {
                OutputRecHalves = new[] { Helper.ArrayOfFunction(i => new float[DimsVolumeCropped.ElementsSlice()], DimsVolumeCropped.Z),
                                          Helper.ArrayOfFunction(i => new float[DimsVolumeCropped.ElementsSlice()], DimsVolumeCropped.Z)};
            }

            int NThreads = 1;

            int[] PlanForw = new int[NThreads], PlanBack = new int[NThreads], PlanForwCTF = new int[NThreads];
            for (int i = 0; i < NThreads; i++)
                Projector.GetPlans(new int3(SizeSubPadded), 2, out PlanForw[i], out PlanBack[i], out PlanForwCTF[i]);
            int[] PlanForwParticle = Helper.ArrayOfFunction(i => GPU.CreateFFTPlan(new int3(SizeSubPadded, SizeSubPadded, 1), (uint)NTilts), NThreads);
            Projector[] Projectors = Helper.ArrayOfFunction(i => new Projector(new int3(SizeSubPadded), 2), NThreads);

            Image[] Subtomo = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubPadded)), NThreads);
            Image[] SubtomoCropped = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSub)), NThreads);

            Image[] Images = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubPadded, SizeSubPadded, NTilts)), NThreads);
            Image[] ImagesFT = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubPadded, SizeSubPadded, NTilts), true, true), NThreads);
            Image[] ImagesFTHalf = options.PrepareDenoising ? Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubPadded, SizeSubPadded, NTilts), true, true), NThreads) : null;
            Image[] CTFs = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubPadded, SizeSubPadded, NTilts), true), NThreads);
            Image[] CTFsHalf = options.PrepareDenoising ? Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubPadded, SizeSubPadded, NTilts), true), NThreads) : null;

            #endregion

            #region Reconstruction

            int NDone = 0;

            Helper.ForCPU(0, GridCoords.Count, NThreads,
                threadID => GPU.SetDevice(GPUID),
                (p, threadID) =>
                {
                    // if (IsCanceled)
                    //     return;

                    float3 CoordsPhysical = GridCoords[p] * (float)options.BinnedPixelSizeMean;

                    GetImagesForOneParticle(options, TiltData, SizeSubPadded, CoordsPhysical, PlanForwParticle[threadID], -1, 8, Images[threadID], ImagesFT[threadID]);
                    GetCTFsForOneParticle(options, CoordsPhysical, CTFCoords, null, true, false, false, CTFs[threadID]);

                    ImagesFT[threadID].Multiply(CTFs[threadID]);    // Weight and phase-flip image FTs
                    CTFs[threadID].Abs();                 // No need for Wiener, just phase flipping

                    #region Normal reconstruction

                    {
                        Projectors[threadID].Data.Fill(0);
                        Projectors[threadID].Weights.Fill(0);

                        Projectors[threadID].BackProject(ImagesFT[threadID], CTFs[threadID], GetAngleInAllTilts(CoordsPhysical), MagnificationCorrection);
                        Projectors[threadID].Reconstruct(Subtomo[threadID].GetDevice(Intent.Write), false, "C1", PlanForw[threadID], PlanBack[threadID], PlanForwCTF[threadID]);

                        GPU.Pad(Subtomo[threadID].GetDevice(Intent.Read),
                                SubtomoCropped[threadID].GetDevice(Intent.Write),
                                new int3(SizeSubPadded),
                                new int3(SizeSub),
                                1);

                        float[][] SubtomoData = SubtomoCropped[threadID].GetHost(Intent.Read);

                        int3 Origin = new int3(GridCoords[p]) - SizeSub / 2;
                        for (int z = 0; z < SizeSub; z++)
                        {
                            int zVol = Origin.Z + z;
                            if (zVol >= DimsVolumeCropped.Z)
                                continue;

                            for (int y = 0; y < SizeSub; y++)
                            {
                                int yVol = Origin.Y + y;
                                if (yVol >= DimsVolumeCropped.Y)
                                    continue;

                                for (int x = 0; x < SizeSub; x++)
                                {
                                    int xVol = Origin.X + x;
                                    if (xVol >= DimsVolumeCropped.X)
                                        continue;

                                    OutputRec[zVol][yVol * DimsVolumeCropped.X + xVol] = SubtomoData[z][y * SizeSub + x];
                                }
                            }
                        }
                        // Console.WriteLine("Reconstruction Done in Tiltseries.cs");
                    }

                    #endregion

                    #region Odd/even tilt reconstruction

                    if (options.PrepareDenoising)
                    {
                        for (int ihalf = 0; ihalf < 2; ihalf++)
                        {
                            GPU.CopyDeviceToDevice(ImagesFT[threadID].GetDevice(Intent.Read),
                                                   ImagesFTHalf[threadID].GetDevice(Intent.Write),
                                                   ImagesFT[threadID].ElementsReal);
                            GPU.CopyDeviceToDevice(CTFs[threadID].GetDevice(Intent.Read),
                                                   CTFsHalf[threadID].GetDevice(Intent.Write),
                                                   CTFs[threadID].ElementsReal);
                            ImagesFTHalf[threadID].Multiply(Helper.ArrayOfFunction(i => i % 2 == ihalf ? 1f : 0f, NTilts));
                            CTFsHalf[threadID].Multiply(Helper.ArrayOfFunction(i => i % 2 == ihalf ? 1f : 0f, NTilts));

                            Projectors[threadID].Data.Fill(0);
                            Projectors[threadID].Weights.Fill(0);

                            Projectors[threadID].BackProject(ImagesFTHalf[threadID], CTFsHalf[threadID], GetAngleInAllTilts(CoordsPhysical), MagnificationCorrection);
                            Projectors[threadID].Reconstruct(Subtomo[threadID].GetDevice(Intent.Write), false, "C1", PlanForw[threadID], PlanBack[threadID], PlanForwCTF[threadID]);

                            GPU.Pad(Subtomo[threadID].GetDevice(Intent.Read),
                                    SubtomoCropped[threadID].GetDevice(Intent.Write),
                                    new int3(SizeSubPadded),
                                    new int3(SizeSub),
                                    1);

                            float[][] SubtomoData = SubtomoCropped[threadID].GetHost(Intent.Read);

                            int3 Origin = new int3(GridCoords[p]) - SizeSub / 2;
                            for (int z = 0; z < SizeSub; z++)
                            {
                                int zVol = Origin.Z + z;
                                if (zVol >= DimsVolumeCropped.Z)
                                    continue;

                                for (int y = 0; y < SizeSub; y++)
                                {
                                    int yVol = Origin.Y + y;
                                    if (yVol >= DimsVolumeCropped.Y)
                                        continue;

                                    for (int x = 0; x < SizeSub; x++)
                                    {
                                        int xVol = Origin.X + x;
                                        if (xVol >= DimsVolumeCropped.X)
                                            continue;

                                        OutputRecHalves[ihalf][zVol][yVol * DimsVolumeCropped.X + xVol] = SubtomoData[z][y * SizeSub + x];
                                    }
                                }
                            }
                        }
                    }

                    #endregion

                    // lock (OutputRec)
                    //     if (progressCallback != null)
                    //         IsCanceled = progressCallback(Grid, ++NDone, "Reconstructing...");
                }, null);

            #region Teardown

            for (int i = 0; i < NThreads; i++)
            {
                GPU.DestroyFFTPlan(PlanForw[i]);
                GPU.DestroyFFTPlan(PlanBack[i]);
                GPU.DestroyFFTPlan(PlanForwCTF[i]);
                GPU.DestroyFFTPlan(PlanForwParticle[i]);
                Projectors[i].Dispose();
                Subtomo[i].Dispose();
                SubtomoCropped[i].Dispose();
                Images[i].Dispose();
                ImagesFT[i].Dispose();
                CTFs[i].Dispose();
                if (options.PrepareDenoising)
                {
                    ImagesFTHalf[i].Dispose();
                    CTFsHalf[i].Dispose();
                }
            }

            CTFCoords.Dispose();
            foreach (var image in TiltData)
                image.FreeDevice();
            foreach (var tiltMask in TiltMasks)
                tiltMask?.FreeDevice();

            #endregion

            if (IsCanceled)
                return;

            if (options.DoDeconv)
            {
                // IsCanceled = progressCallback(Grid, (int)Grid.Elements(), "Deconvolving...");

                {
                    Image FullRec = new Image(OutputRec, DimsVolumeCropped);

                    Image FullRecFT = FullRec.AsFFT_CPU();
                    FullRec.Dispose();

                    CTF SubtomoCTF = CTF.GetCopy();
                    SubtomoCTF.Defocus = (decimal)GetTiltDefocus(NTilts / 2);
                    SubtomoCTF.PixelSize = options.BinnedPixelSizeMean;

                    GPU.DeconvolveCTF(FullRecFT.GetDevice(Intent.Read),
                                        FullRecFT.GetDevice(Intent.Write),
                                        FullRecFT.Dims,
                                        SubtomoCTF.ToStruct(),
                                        (float)options.DeconvStrength,
                                        (float)options.DeconvFalloff,
                                        (float)(options.BinnedPixelSizeMean * 2 / options.DeconvHighpass));

                    Image FullRecDeconv = FullRecFT.AsIFFT_CPU();
                    FullRecFT.Dispose();

                    OutputRecDeconv = FullRecDeconv.GetHost(Intent.Read);
                    FullRecDeconv.Dispose();
                }

                if (options.PrepareDenoising)
                {
                    for (int ihalf = 0; ihalf < 2; ihalf++)
                    {
                        Image FullRec = new Image(OutputRecHalves[ihalf], DimsVolumeCropped);

                        Image FullRecFT = FullRec.AsFFT_CPU();
                        FullRec.Dispose();

                        CTF SubtomoCTF = CTF.GetCopy();
                        SubtomoCTF.Defocus = (decimal)GetTiltDefocus(NTilts / 2);
                        SubtomoCTF.PixelSize = options.BinnedPixelSizeMean;

                        GPU.DeconvolveCTF(FullRecFT.GetDevice(Intent.Read),
                                            FullRecFT.GetDevice(Intent.Write),
                                            FullRecFT.Dims,
                                            SubtomoCTF.ToStruct(),
                                            (float)options.DeconvStrength,
                                            (float)options.DeconvFalloff,
                                            (float)(options.BinnedPixelSizeMean * 2 / options.DeconvHighpass));

                        Image FullRecDeconv = FullRecFT.AsIFFT_CPU();
                        FullRecFT.Dispose();

                        OutputRecHalves[ihalf] = FullRecDeconv.GetHost(Intent.Read);
                        FullRecDeconv.Dispose();
                    }
                }
            }

            if (options.KeepOnlyFullVoxels)
            {
                // IsCanceled = progressCallback(Grid, (int)Grid.Elements(), "Trimming...");

                float BinnedAngPix = (float)options.BinnedPixelSizeMean;

                Parallel.For(0, DimsVolumeCropped.Z, z =>
                {
                    float3[] VolumePositions = new float3[DimsVolumeCropped.ElementsSlice()];
                    for (int y = 0; y < DimsVolumeCropped.Y; y++)
                        for (int x = 0; x < DimsVolumeCropped.X; x++)
                            VolumePositions[y * DimsVolumeCropped.X + x] = new float3(x * BinnedAngPix, y * BinnedAngPix, z * BinnedAngPix);

                    float3[] ImagePositions = GetPositionInAllTiltsNoLocalWarp(VolumePositions);

                    for (int i = 0; i < ImagePositions.Length; i++)
                    {
                        int ii = i / NTilts;
                        int t = i % NTilts;

                        if (ImagePositions[i].X < 0 || ImagePositions[i].Y < 0 ||
                            ImagePositions[i].X > ImageDimensionsPhysical.X - BinnedAngPix ||
                            ImagePositions[i].Y > ImageDimensionsPhysical.Y - BinnedAngPix)
                        {
                            OutputRec[z][ii] = 0;
                            if (options.DoDeconv)
                                OutputRecDeconv[z][ii] = 0;
                            if (options.PrepareDenoising)
                            {
                                OutputRecHalves[0][z][ii] = 0;
                                OutputRecHalves[1][z][ii] = 0;
                            }
                        }
                    }
                });
            }

            #endregion

            // IsCanceled = progressCallback(Grid, (int)Grid.Elements(), "Writing...");
            Console.WriteLine($"Writing.... {(float)options.BinnedPixelSizeMean}px");
            
            Image OutputRecImage = new Image(OutputRec, DimsVolumeCropped);
            OutputRecImage.WriteMRC(ReconstructionDir + NameWithRes + ".mrc", (float)options.BinnedPixelSizeMean, true);
            OutputRecImage.Dispose();

            if (options.DoDeconv)
            {
                Image OutputRecDeconvImage = new Image(OutputRecDeconv, DimsVolumeCropped);
                OutputRecDeconvImage.WriteMRC(ReconstructionDeconvDir + NameWithRes + ".mrc", (float)options.BinnedPixelSizeMean, true);
                OutputRecDeconvImage.Dispose();
            }

            if (options.PrepareDenoising)
            {
                Image OutputRecOddImage = new Image(OutputRecHalves[0], DimsVolumeCropped);
                OutputRecOddImage.WriteMRC(ReconstructionOddDir + NameWithRes + ".mrc", (float)options.BinnedPixelSizeMean, true);
                OutputRecOddImage.Dispose();

                Image OutputRecEvenImage = new Image(OutputRecHalves[1], DimsVolumeCropped);
                OutputRecEvenImage.WriteMRC(ReconstructionEvenDir + NameWithRes + ".mrc", (float)options.BinnedPixelSizeMean, true);
                OutputRecEvenImage.Dispose();
            }

            // IsCanceled = progressCallback(Grid, (int)Grid.Elements(), "Done.");
        }

        public void ReconstructSubtomos(ProcessingOptionsTomoSubReconstruction options, float3[] positions, float3[] angles)
        {
            int GPUID = GPU.GetDevice();

            bool IsCanceled = false;

            if (!Directory.Exists(SubtomoDir))
                Directory.CreateDirectory(SubtomoDir);

            #region Dimensions

            VolumeDimensionsPhysical = options.DimensionsPhysical;

            CTF MaxDefocusCTF = GetTiltCTF(IndicesSortedDose[0]);
            MaxDefocusCTF.PixelSize = options.BinnedPixelSizeMean;
            int MinimumBoxSize = (int)Math.Round(MaxDefocusCTF.GetAliasingFreeSize((float)options.BinnedPixelSizeMean * 2) / 2f) * 2;

            int SizeSub = options.BoxSize;
            int SizeSubSuper = Math.Max(SizeSub * 2, MinimumBoxSize);

            #endregion

            #region Load and preprocess tilt series

            Movie[] TiltMovies;
            Image[] TiltData;
            Image[] TiltMasks;
            LoadMovieData(options, true, out TiltMovies, out TiltData);
            LoadMovieMasks(options, out TiltMasks);
            for (int z = 0; z < NTilts; z++)
            {
                EraseDirt(TiltData[z], TiltMasks[z]);
                TiltMasks[z]?.FreeDevice();

                if (options.NormalizeInput)
                {
                    TiltData[z].SubtractMeanGrid(new int2(1));
                    TiltData[z].Bandpass(1f / SizeSub, 1f, false, 0f);

                    GPU.Normalize(TiltData[z].GetDevice(Intent.Read),
                                  TiltData[z].GetDevice(Intent.Write),
                                  (uint)TiltData[z].ElementsReal,
                                  1);
                }

                if (options.Invert)
                    TiltData[z].Multiply(-1f);

                TiltData[z].FreeDevice();

                //TiltData[z].Multiply(TiltMasks[z]);
            }

            #endregion

            #region Memory and FFT plan allocation

            Image CTFCoords = CTF.GetCTFCoords(SizeSubSuper, SizeSubSuper);

            int NThreads = 1;

            int[] PlanForwRec = new int[NThreads], PlanBackRec = new int[NThreads];
            for (int i = 0; i < NThreads; i++)
            {
                //Projector.GetPlans(new int3(SizeSubSuper), 1, out PlanForwRec[i], out PlanBackRec[i], out PlanForwCTF[i]);
                PlanForwRec[i] = GPU.CreateFFTPlan(new int3(SizeSubSuper), 1);
                PlanBackRec[i] = GPU.CreateIFFTPlan(new int3(SizeSubSuper), 1);
            }
            int[] PlanForwRecCropped = Helper.ArrayOfFunction(i => GPU.CreateFFTPlan(new int3(SizeSub), 1), NThreads);
            int[] PlanForwParticle = Helper.ArrayOfFunction(i => GPU.CreateFFTPlan(new int3(SizeSubSuper, SizeSubSuper, 1), (uint)NTilts), NThreads);

            Projector[] Projectors = Helper.ArrayOfFunction(i => new Projector(new int3(SizeSubSuper), 1), NThreads);

            Image[] VolumeCropped = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSub)), NThreads);
            Image[] VolumeCTFCropped = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSub), true), NThreads);

            Image[] Subtomo = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper)), NThreads);
            //Image[] SubtomoCTF = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper), true), NThreads);
            //Image[] SubtomoCTFComplex = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper), true, true), NThreads);
            Image[] SubtomoSparsityMask = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSub), true), NThreads);
            Image[] Images = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper, SizeSubSuper, NTilts)), NThreads);
            Image[] ImagesFT = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper, SizeSubSuper, NTilts), true, true), NThreads);
            Image[] CTFs = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper, SizeSubSuper, NTilts), true), NThreads);
            Image[] CTFsAbs = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper, SizeSubSuper, NTilts), true), NThreads);
            Image[] CTFsUnweighted = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper, SizeSubSuper, NTilts), true), NThreads);
            Image[] CTFsComplex = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, new int3(SizeSubSuper, SizeSubSuper, NTilts), true, true), NThreads);

            GPU.CheckGPUExceptions();

            #endregion

            Helper.ForCPU(0, positions.Length / NTilts, NThreads,
                threadID => GPU.SetDevice(GPUID),
                (p, threadID) =>
                {

                    if (IsCanceled)
                        return;

                    float3[] ParticlePositions = positions.Skip(p * NTilts).Take(NTilts).ToArray();
                    float3[] ParticleAngles = options.PrerotateParticles ? angles.Skip(p * NTilts).Take(NTilts).ToArray() : null;

                    Timing.Start("ExtractImageData");
                    GetImagesForOneParticle(options, TiltData, SizeSubSuper, ParticlePositions, PlanForwParticle[threadID], -1, 8, true, Images[threadID], ImagesFT[threadID]);
                    Timing.Finish("ExtractImageData");

                    Timing.Start("CreateRawCTF");
                    GetCTFsForOneParticle(options, ParticlePositions, CTFCoords, null, true, false, false, CTFs[threadID]);
                    GetCTFsForOneParticle(options, ParticlePositions, CTFCoords, null, false, false, false, CTFsUnweighted[threadID]);
                    Timing.Finish("CreateRawCTF");

                    // Subtomo is (Image * CTFweighted) / abs(CTFunweighted)
                    // 3D CTF is (CTFweighted * CTFweighted) / abs(CTFweighted)

                    ImagesFT[threadID].Multiply(CTFs[threadID]);
                    GPU.Abs(CTFs[threadID].GetDevice(Intent.Read),
                            CTFsAbs[threadID].GetDevice(Intent.Write),
                            CTFs[threadID].ElementsReal);
                    CTFsUnweighted[threadID].Abs();

                    CTFsComplex[threadID].Fill(new float2(1, 0));
                    CTFsComplex[threadID].Multiply(CTFs[threadID]);
                    CTFsComplex[threadID].Multiply(CTFs[threadID]);

                    #region Sub-tomo

                    Projectors[threadID].Data.Fill(0);
                    Projectors[threadID].Weights.Fill(0);

                    Timing.Start("ProjectImageData");
                    Projectors[threadID].BackProject(ImagesFT[threadID], CTFsUnweighted[threadID], !options.PrerotateParticles ? GetAngleInAllTilts(ParticlePositions) : GetParticleAngleInAllTilts(ParticlePositions, ParticleAngles), MagnificationCorrection);
                    Timing.Finish("ProjectImageData");
                    Timing.Start("ReconstructSubtomo");
                    Projectors[threadID].Reconstruct(Subtomo[threadID].GetDevice(Intent.Write), false, "C1", PlanForwRec[threadID], PlanBackRec[threadID], PlanForwRec[threadID], 0);
                    Timing.Finish("ReconstructSubtomo");

                    GPU.Pad(Subtomo[threadID].GetDevice(Intent.Read),
                            VolumeCropped[threadID].GetDevice(Intent.Write),
                            new int3(SizeSubSuper),
                            new int3(SizeSub),
                            1);

                    if (options.NormalizeOutput)
                        GPU.NormParticles(VolumeCropped[threadID].GetDevice(Intent.Read),
                                          VolumeCropped[threadID].GetDevice(Intent.Write),
                                          new int3(SizeSub),
                                          (uint)Math.Round(options.ParticleDiameter / options.BinnedPixelSizeMean / 2),
                                          false,
                                          1);

                    VolumeCropped[threadID].WriteMRC(SubtomoDir + $"{RootName}{options.Suffix}_{p:D7}_{options.BinnedPixelSizeMean:F2}A.mrc", (float)options.BinnedPixelSizeMean, true);

                    #endregion

                    #region CTF

                    // Back-project and reconstruct
                    Projectors[threadID].Data.Fill(0);
                    Projectors[threadID].Weights.Fill(0);

                    Projectors[threadID].BackProject(CTFsComplex[threadID], CTFsAbs[threadID], !options.PrerotateParticles ? GetAngleInAllTilts(ParticlePositions) : GetParticleAngleInAllTilts(ParticlePositions, ParticleAngles), MagnificationCorrection);
                    Timing.Start("ReconstructCTF");
                    Projectors[threadID].Reconstruct(Subtomo[threadID].GetDevice(Intent.Write), false, "C1", PlanForwRec[threadID], PlanBackRec[threadID], PlanForwRec[threadID], 0);
                    Timing.Finish("ReconstructCTF");

                    Timing.Start("3DCTFCrop");
                    //SubtomoCTFComplex[threadID].Fill(new float2(1, 0));
                    //SubtomoCTFComplex[threadID].Multiply(SubtomoCTF[threadID]);
                    //GPU.IFFT(SubtomoCTFComplex[threadID].GetDevice(Intent.Read),
                    //         Subtomo[threadID].GetDevice(Intent.Write),
                    //         new int3(SizeSubSuper),
                    //         1,
                    //         PlanBackRec[threadID],
                    //         false);

                    GPU.Pad(Subtomo[threadID].GetDevice(Intent.Read),
                            VolumeCropped[threadID].GetDevice(Intent.Write),
                            new int3(SizeSubSuper),
                            new int3(SizeSub),
                            1);

                    GPU.FFT(VolumeCropped[threadID].GetDevice(Intent.Read),
                            Subtomo[threadID].GetDevice(Intent.Write),
                            new int3(SizeSub),
                            1,
                            PlanForwRecCropped[threadID]);

                    GPU.ShiftStackFT(Subtomo[threadID].GetDevice(Intent.Read),
                                     Subtomo[threadID].GetDevice(Intent.Write),
                                     new int3(SizeSub),
                                     new[] { SizeSub / 2f, SizeSub / 2f, SizeSub / 2f },
                                     1);

                    GPU.Real(Subtomo[threadID].GetDevice(Intent.Read),
                             VolumeCTFCropped[threadID].GetDevice(Intent.Write),
                             VolumeCTFCropped[threadID].ElementsReal);

                    VolumeCTFCropped[threadID].Multiply(1f / (SizeSubSuper * SizeSubSuper));
                    Timing.Finish("3DCTFCrop");

                    if (options.MakeSparse)
                    {
                        GPU.Abs(VolumeCTFCropped[threadID].GetDevice(Intent.Read),
                                SubtomoSparsityMask[threadID].GetDevice(Intent.Write),
                                VolumeCTFCropped[threadID].ElementsReal);
                        SubtomoSparsityMask[threadID].Binarize(0.01f);

                        VolumeCTFCropped[threadID].Multiply(SubtomoSparsityMask[threadID]);
                    }

                    VolumeCTFCropped[threadID].WriteMRC(SubtomoDir + $"{RootName}{options.Suffix}_{p:D7}_ctf_{options.BinnedPixelSizeMean:F2}A.mrc", (float)options.BinnedPixelSizeMean, true);

                    #endregion

                    //Console.WriteLine(SizeSubSuper);
                    //Timing.PrintMeasurements();
                }, null);

            #region Teardown

            for (int i = 0; i < NThreads; i++)
            {
                GPU.DestroyFFTPlan(PlanForwRec[i]);
                GPU.DestroyFFTPlan(PlanBackRec[i]);
                //GPU.DestroyFFTPlan(PlanForwCTF[i]);
                GPU.DestroyFFTPlan(PlanForwParticle[i]);
                Projectors[i].Dispose();
                Subtomo[i].Dispose();
                //SubtomoCTF[i].Dispose();
                SubtomoSparsityMask[i].Dispose();
                Images[i].Dispose();
                ImagesFT[i].Dispose();
                CTFs[i].Dispose();
                CTFsAbs[i].Dispose();
                CTFsUnweighted[i].Dispose();
                CTFsComplex[i].Dispose();

                GPU.DestroyFFTPlan(PlanForwRecCropped[i]);
                VolumeCropped[i].Dispose();
                VolumeCTFCropped[i].Dispose();
                //SubtomoCTFComplex[i].Dispose();
            }

            CTFCoords.Dispose();
            //CTFCoordsPadded.Dispose();
            foreach (var image in TiltData)
                image.FreeDevice();
            foreach (var tiltMask in TiltMasks)
                tiltMask?.FreeDevice();

            #endregion
        }

        public void ReconstructParticleSeries(ProcessingOptionsTomoSubReconstruction options, float3[] positions, float3[] angles, int[] subsets, string tablePath, out Star tableOut)
        {
            bool IsCanceled = false;

            if (!Directory.Exists(ParticleSeriesDir))
                Directory.CreateDirectory(ParticleSeriesDir);

            #region Dimensions

            VolumeDimensionsPhysical = options.DimensionsPhysical;

            int SizeSub = options.BoxSize;

            #endregion

            #region Load and preprocess tilt series

            Movie[] TiltMovies;
            Image[] TiltData;
            Image[] TiltMasks;
            LoadMovieData(options, true, out TiltMovies, out TiltData);
            LoadMovieMasks(options, out TiltMasks);
            for (int z = 0; z < NTilts; z++)
            {
                EraseDirt(TiltData[z], TiltMasks[z]);
                TiltMasks[z]?.FreeDevice();

                if (options.NormalizeInput)
                {
                    TiltData[z].SubtractMeanGrid(new int2(1));
                    TiltData[z].Bandpass(1f / SizeSub, 1f, false, 0f);

                    GPU.Normalize(TiltData[z].GetDevice(Intent.Read),
                                  TiltData[z].GetDevice(Intent.Write),
                                  (uint)TiltData[z].ElementsReal,
                                  1);
                }

                if (options.Invert)
                    TiltData[z].Multiply(-1f);

                //TiltData[z].Multiply(TiltMasks[z]);
            }

            #endregion

            #region Memory and FFT plan allocation

            int PlanForwParticle = GPU.CreateFFTPlan(new int3(SizeSub, SizeSub, 1), (uint)NTilts);
            int PlanBackParticle = GPU.CreateIFFTPlan(new int3(SizeSub, SizeSub, 1), (uint)NTilts);

            #endregion

            #region Create STAR table

            tableOut = new Star(new string[]
            {
                "rlnMagnification",
                "rlnDetectorPixelSize",
                "rlnVoltage",
                "rlnSphericalAberration",
                "rlnAmplitudeContrast",
                "rlnPhaseShift",
                "rlnDefocusU",
                "rlnDefocusV",
                "rlnDefocusAngle",
                "rlnImageName",
                "rlnMicrographName",
                "rlnCoordinateX",
                "rlnCoordinateY",
                "rlnAngleRot",
                "rlnAngleTilt",
                "rlnAnglePsi",
                "rlnCtfBfactor",
                "rlnCtfScalefactor",
                "rlnRandomSubset",
                "rlnGroupName"
            });

            #endregion

            Random Rand = new Random(Name.GetHashCode());

            CTF[] TiltCTFs = Helper.ArrayOfFunction(t => GetTiltCTF(t), NTilts);
            float PixelSize = (float)options.BinnedPixelSizeMean;

            int[] UsedTilts = options.DoLimitDose ? IndicesSortedDose.Take(options.NTilts).ToArray() : IndicesSortedDose;

            Image ImagesSorted = new Image(new int3(SizeSub, SizeSub, UsedTilts.Length));
            Image Images = new Image(new int3(SizeSub, SizeSub, NTilts));
            Image ImagesFT = new Image(new int3(SizeSub, SizeSub, NTilts), true, true);

            for (int p = 0; p < positions.Length / NTilts; p++)
            {
                float3[] ParticlePositions = positions.Skip(p * NTilts).Take(NTilts).ToArray();
                float3[] ParticleAngles = angles.Skip(p * NTilts).Take(NTilts).ToArray();

                CTF[] WeightParams = Helper.ArrayOfFunction(t => GetCTFParamsForOneTilt(1, new float[1], new[] { ParticlePositions[t] }, t, true, true, true)[0], NTilts);

                ImagesFT = GetImagesForOneParticle(options, TiltData, SizeSub, ParticlePositions, PlanForwParticle, -1, 0, false, Images, ImagesFT);
                GPU.IFFT(ImagesFT.GetDevice(Intent.Read),
                         Images.GetDevice(Intent.Write),
                         ImagesFT.Dims.Slice(),
                         (uint)ImagesFT.Dims.Z,
                         PlanBackParticle,
                         false);

                float[][] ImagesData = Images.GetHost(Intent.Read);
                float[][] ImagesSortedData = ImagesSorted.GetHost(Intent.Write);
                for (int i = 0; i < UsedTilts.Length; i++)
                    ImagesSortedData[i] = ImagesData[UsedTilts[i]];

                string SeriesPath = ParticleSeriesDir + $"{RootName}{options.Suffix}_{p:D7}_{options.BinnedPixelSizeMean:F2}A.mrcs";

                ImagesSorted.WriteMRC(SeriesPath, true);

                Uri UriStar = new Uri(tablePath);
                SeriesPath = UriStar.MakeRelativeUri(new Uri(SeriesPath)).ToString();

                float3[] ImagePositions = GetPositionInAllTilts(ParticlePositions);
                float3[] ImageAngles = GetParticleAngleInAllTilts(ParticlePositions, ParticleAngles).Select(a => a * Helper.ToDeg).ToArray();

                for (int i = 0; i < UsedTilts.Length; i++)
                {
                    int t = UsedTilts[i];
                    tableOut.AddRow(new List<string>()
                    {
                        "10000",
                        PixelSize.ToString("F5", CultureInfo.InvariantCulture),
                        CTF.Voltage.ToString("F1", CultureInfo.InvariantCulture),
                        CTF.Cs.ToString("F4", CultureInfo.InvariantCulture),
                        CTF.Amplitude.ToString("F3", CultureInfo.InvariantCulture),
                        (TiltCTFs[t].PhaseShift * 180).ToString("F1", CultureInfo.InvariantCulture),
                        ((ImagePositions[t].Z + (float)TiltCTFs[t].DefocusDelta / 2) * 1e4).ToString("F1", CultureInfo.InvariantCulture),
                        ((ImagePositions[t].Z - (float)TiltCTFs[t].DefocusDelta / 2) * 1e4).ToString("F1", CultureInfo.InvariantCulture),
                        TiltCTFs[t].DefocusAngle.ToString("F1", CultureInfo.InvariantCulture),
                        (i + 1).ToString("D3") + "@" + SeriesPath,
                        TiltMovies[t].Name,
                        (ImagePositions[t].X / PixelSize).ToString("F5", CultureInfo.InvariantCulture),
                        (ImagePositions[t].Y / PixelSize).ToString("F5", CultureInfo.InvariantCulture),
                        ImageAngles[t].X.ToString("F5", CultureInfo.InvariantCulture),
                        ImageAngles[t].Y.ToString("F5", CultureInfo.InvariantCulture),
                        ImageAngles[t].Z.ToString("F5", CultureInfo.InvariantCulture),
                        WeightParams[t].Bfactor.ToString("F2", CultureInfo.InvariantCulture),
                        WeightParams[t].Scale.ToString("F4", CultureInfo.InvariantCulture),
                        subsets[p].ToString(),
                        $"{RootName}{options.Suffix}_{p:D7}"
                    });
                }

                if (IsCanceled)
                    break;
            }

            #region Teardown

            ImagesSorted.Dispose();
            Images.Dispose();
            ImagesFT.Dispose();

            GPU.DestroyFFTPlan(PlanForwParticle);
            GPU.DestroyFFTPlan(PlanBackParticle);

            foreach (var image in TiltData)
                image.FreeDevice();
            foreach (var tiltMask in TiltMasks)
                tiltMask?.FreeDevice();

            #endregion
        }

        #endregion

        #region Multi-particle refinement

        public override void PerformMultiParticleRefinement(string workingDirectory,
                                                            ProcessingOptionsMPARefine optionsMPA,
                                                            Species[] allSpecies,
                                                            DataSource dataSource,
                                                            Image gainRef,
                                                            DefectModel defectMap,
                                                            Action<string> progressCallback)
        {
            int GPUID = GPU.GetDevice();
            HeaderEER.GroupNFrames = dataSource.EERGroupFrames;

            float BfactorWeightingThreshold = (float)optionsMPA.BFactorWeightingThreshold;

            //AreAnglesInverted = false;

            //MagnificationCorrection = new float3(1, 1, 0);

            if (CTF.ZernikeCoeffsOdd == null)
                CTF.ZernikeCoeffsOdd = new float[12];
            else if (CTF.ZernikeCoeffsOdd.Length < 12)
                CTF.ZernikeCoeffsOdd = Helper.Combine(CTF.ZernikeCoeffsOdd, new float[12 - CTF.ZernikeCoeffsOdd.Length]);

            if (CTF.ZernikeCoeffsEven == null)
                CTF.ZernikeCoeffsEven = new float[8];
            else if (CTF.ZernikeCoeffsEven.Length < 8)
                CTF.ZernikeCoeffsEven = Helper.Combine(CTF.ZernikeCoeffsEven, new float[8 - CTF.ZernikeCoeffsEven.Length]);

            #region Get particles belonging to this item; if there are none, abort

            string DataHash = GetDataHash();

            Dictionary<Species, Particle[]> SpeciesParticles = new Dictionary<Species, Particle[]>();
            foreach (var species in allSpecies)
                SpeciesParticles.Add(species, species.GetParticles(DataHash));

            if (!SpeciesParticles.Any(p => p.Value.Length > 0))
                return;

            #endregion

            #region Figure out dimensions

            float SmallestAngPix = MathHelper.Min(allSpecies.Select(s => (float)s.PixelSize));
            float LargestBox = MathHelper.Max(allSpecies.Select(s => s.DiameterAngstrom)) * 2 / SmallestAngPix;

            float MinDose = MathHelper.Min(Dose), MaxDose = MathHelper.Max(Dose);
            float[] DoseInterpolationSteps = Dose.Select(d => (d - MinDose) / (MaxDose - MinDose)).ToArray();

            #endregion

            #region Load and preprocess tilt series

            progressCallback("Loading tilt series and masks...");

            decimal BinTimes = (decimal)Math.Log(SmallestAngPix / (float)dataSource.PixelSizeMean, 2.0);
            ProcessingOptionsTomoSubReconstruction OptionsDataLoad = new ProcessingOptionsTomoSubReconstruction()
            {
                PixelSizeX = dataSource.PixelSizeX,
                PixelSizeY = dataSource.PixelSizeY,
                PixelSizeAngle = dataSource.PixelSizeAngle,

                BinTimes = BinTimes,
                EERGroupFrames = dataSource.EERGroupFrames,
                GainPath = dataSource.GainPath,
                GainHash = "",
                GainFlipX = dataSource.GainFlipX,
                GainFlipY = dataSource.GainFlipY,
                GainTranspose = dataSource.GainTranspose,
                DefectsPath = dataSource.DefectsPath,
                DefectsHash = "",

                Dimensions = new float3((float)dataSource.DimensionsX,
                                        (float)dataSource.DimensionsY,
                                        (float)dataSource.DimensionsZ),

                Invert = true,
                NormalizeInput = true,
                NormalizeOutput = false,

                PrerotateParticles = true
            };

            VolumeDimensionsPhysical = OptionsDataLoad.DimensionsPhysical;

            Movie[] TiltMovies = null;
            Image[] TiltData = null;
            Image[] TiltMasks = null;

            Action LoadAndPreprocessTiltData = () =>
            {
                LoadMovieData(OptionsDataLoad, true, out TiltMovies, out TiltData);
                LoadMovieMasks(OptionsDataLoad, out TiltMasks);
                for (int z = 0; z < NTilts; z++)
                {
                    EraseDirt(TiltData[z], TiltMasks[z]);
                    TiltMasks[z]?.FreeDevice();

                    TiltData[z].SubtractMeanGrid(new int2(1));
                    TiltData[z].Bandpass(1f / LargestBox, 1f, false, 0f);

                    GPU.Normalize(TiltData[z].GetDevice(Intent.Read),
                                    TiltData[z].GetDevice(Intent.Write),
                                    (uint)TiltData[z].ElementsReal,
                                    1);

                    TiltData[z].Multiply(-1f);
                    //TiltData[z].Multiply(TiltMasks[z]);

                    //TiltData[z].FreeDevice();
                }
            };
            LoadAndPreprocessTiltData();

            Console.WriteLine($"{GPU.GetFreeMemory(GPUID)} MB after loading raw data of {Name}");

            #endregion

            #region Compose optimization steps based on user's requests

            var OptimizationStepsWarp = new List<(WarpOptimizationTypes Type, int Iterations, string Name)>();
            {
                WarpOptimizationTypes TranslationComponents = 0;
                if (optionsMPA.DoImageWarp)
                    TranslationComponents |= WarpOptimizationTypes.ImageWarp;
                if (optionsMPA.DoVolumeWarp)
                    TranslationComponents |= WarpOptimizationTypes.VolumeWarp;

                if (TranslationComponents != 0)
                    OptimizationStepsWarp.Add((TranslationComponents, 10, "image & volume warping"));
            }
            {
                WarpOptimizationTypes AntisymComponents = 0;

                if (optionsMPA.DoZernike13)
                    AntisymComponents |= WarpOptimizationTypes.Zernike13;
                if (optionsMPA.DoZernike5)
                    AntisymComponents |= WarpOptimizationTypes.Zernike5;

                if (AntisymComponents != 0 && allSpecies.Any(s => s.ResolutionRefinement < (float)optionsMPA.MinimumCTFRefinementResolution))
                    OptimizationStepsWarp.Add((AntisymComponents, 10, "antisymmetrical aberrations"));
            }
            {
                if (optionsMPA.DoAxisAngles)
                    OptimizationStepsWarp.Add((WarpOptimizationTypes.AxisAngle, 6, "stage orientation"));
            }
            {
                WarpOptimizationTypes PoseComponents = 0;
                if (optionsMPA.DoParticlePoses)
                {
                    PoseComponents |= WarpOptimizationTypes.ParticlePosition;
                    PoseComponents |= WarpOptimizationTypes.ParticleAngle;
                }

                if (PoseComponents != 0)
                    OptimizationStepsWarp.Add((PoseComponents, 10, "particle poses"));
            }
            {
                if (optionsMPA.DoMagnification)
                    OptimizationStepsWarp.Add((WarpOptimizationTypes.Magnification, 4, "magnification"));
            }


            var OptimizationStepsCTF = new List<(CTFOptimizationTypes Type, int Iterations, string Name)>();
            {
                CTFOptimizationTypes DefocusComponents = 0;
                if (optionsMPA.DoDefocus)
                    DefocusComponents |= CTFOptimizationTypes.Defocus;
                if (optionsMPA.DoAstigmatismDelta)
                    DefocusComponents |= CTFOptimizationTypes.AstigmatismDelta;
                if (optionsMPA.DoAstigmatismAngle)
                    DefocusComponents |= CTFOptimizationTypes.AstigmatismAngle;
                if (optionsMPA.DoPhaseShift)
                    DefocusComponents |= CTFOptimizationTypes.PhaseShift;
                if (optionsMPA.DoCs)
                    DefocusComponents |= CTFOptimizationTypes.Cs;

                if (DefocusComponents != 0)
                    OptimizationStepsCTF.Add((DefocusComponents, 10, "CTF parameters"));

                CTFOptimizationTypes ZernikeComponents = 0;

                if (optionsMPA.DoZernike2)
                    ZernikeComponents |= CTFOptimizationTypes.Zernike2;
                if (optionsMPA.DoZernike4)
                    ZernikeComponents |= CTFOptimizationTypes.Zernike4;

                if (ZernikeComponents != 0)
                    OptimizationStepsCTF.Add((ZernikeComponents, 10, "symmetrical aberrations"));
            }

            #endregion

            Dictionary<Species, float[]> GoodParticleMasks = new Dictionary<Species, float[]>();

            if (optionsMPA.NIterations > 0)
            {
                #region Resize grids

                int AngleSpatialDim = 1;

                if (optionsMPA.DoAxisAngles)
                    if (GridAngleX == null || GridAngleX.Dimensions.X < AngleSpatialDim || GridAngleX.Dimensions.Z != NTilts)
                    {
                        GridAngleX = GridAngleX == null ? new CubicGrid(new int3(AngleSpatialDim, AngleSpatialDim, NTilts)) :
                                                          GridAngleX.Resize(new int3(AngleSpatialDim, AngleSpatialDim, NTilts));
                        GridAngleY = GridAngleY == null ? new CubicGrid(new int3(AngleSpatialDim, AngleSpatialDim, NTilts)) :
                                                          GridAngleY.Resize(new int3(AngleSpatialDim, AngleSpatialDim, NTilts));
                        GridAngleZ = GridAngleZ == null ? new CubicGrid(new int3(AngleSpatialDim, AngleSpatialDim, NTilts)) :
                                                          GridAngleZ.Resize(new int3(AngleSpatialDim, AngleSpatialDim, NTilts));
                    }

                // Super crude way of figuring out how many parameters can be refined into the available particle signal
                //float OverallMass = 0;
                //foreach (var species in allSpecies)
                //    if (SpeciesParticles.ContainsKey(species))
                //        OverallMass += Math.Max((float)species.MolecularWeightkDa - 100, 0) * SpeciesParticles[species].Length;
                //float NParametersMax = OverallMass / 100 * 5;
                //NParametersMax -= GridAngleX.Values.Length * 3;
                //NParametersMax /= NTilts;
                //int MovementSpatialDim = Math.Min(5, Math.Max(1, (int)Math.Round(Math.Sqrt(NParametersMax))));
                int2 MovementSpatialDims = new int2(optionsMPA.ImageWarpWidth, optionsMPA.ImageWarpHeight);
                //MovementSpatialDim = 2;

                if (optionsMPA.DoImageWarp)
                    if (GridMovementX == null ||
                        GridMovementX.Dimensions.X != MovementSpatialDims.X ||
                        GridMovementX.Dimensions.Y != MovementSpatialDims.Y ||
                        GridMovementX.Dimensions.Z != NTilts)
                    {
                        int3 Dims = new int3(MovementSpatialDims.X, MovementSpatialDims.Y, NTilts);
                        GridMovementX = GridMovementX == null ? new CubicGrid(Dims) : GridMovementX.Resize(Dims);
                        GridMovementY = GridMovementY == null ? new CubicGrid(Dims) : GridMovementY.Resize(Dims);
                    }

                if (optionsMPA.DoVolumeWarp)
                {
                    int4 DimsVolumeWarp = new int4(optionsMPA.VolumeWarpWidth,
                                                   optionsMPA.VolumeWarpHeight,
                                                   optionsMPA.VolumeWarpDepth,
                                                   optionsMPA.VolumeWarpLength);
                    if (GridVolumeWarpX == null || GridVolumeWarpX.Dimensions != DimsVolumeWarp)
                    {
                        GridVolumeWarpX = GridVolumeWarpX == null ? new LinearGrid4D(DimsVolumeWarp) :
                                                                    GridVolumeWarpX.Resize(DimsVolumeWarp);
                        GridVolumeWarpY = GridVolumeWarpY == null ? new LinearGrid4D(DimsVolumeWarp) :
                                                                    GridVolumeWarpY.Resize(DimsVolumeWarp);
                        GridVolumeWarpZ = GridVolumeWarpZ == null ? new LinearGrid4D(DimsVolumeWarp) :
                                                                    GridVolumeWarpZ.Resize(DimsVolumeWarp);
                    }
                }

                #endregion

                #region Create species prerequisites and calculate spectral weights

                progressCallback("Calculating spectral weights...");

                Dictionary<Species, IntPtr[]> SpeciesParticleImages = new Dictionary<Species, IntPtr[]>();
                Dictionary<Species, IntPtr[]> SpeciesParticleQImages = new Dictionary<Species, IntPtr[]>();
                Dictionary<Species, float[]> SpeciesParticleDefoci = new Dictionary<Species, float[]>();
                Dictionary<Species, float2[]> SpeciesParticleExtractedAt = new Dictionary<Species, float2[]>();
                Dictionary<Species, Image> SpeciesTiltWeights = new Dictionary<Species, Image>();
                Dictionary<Species, Image> SpeciesCTFWeights = new Dictionary<Species, Image>();
                Dictionary<Species, IntPtr> SpeciesParticleSubsets = new Dictionary<Species, IntPtr>();
                Dictionary<Species, (int Start, int End)> SpeciesParticleIDRanges = new Dictionary<Species, (int Start, int End)>();
                Dictionary<Species, int> SpeciesRefinementSize = new Dictionary<Species, int>();
                Dictionary<Species, int[]> SpeciesRelevantRefinementSizes = new Dictionary<Species, int[]>();
                Dictionary<Species, int> SpeciesCTFSuperresFactor = new Dictionary<Species, int>();

                Dictionary<Species, Image> CurrentWeightsDict = SpeciesTiltWeights;

                int NParticlesOverall = 0;

                float[][] AverageSpectrum1DAll = Helper.ArrayOfFunction(i => new float[128], NTilts);
                long[][] AverageSpectrum1DAllSamples = Helper.ArrayOfFunction(i => new long[128], NTilts);

                foreach (var species in allSpecies)
                {
                    if (SpeciesParticles[species].Length == 0)
                        continue;

                    Particle[] Particles = SpeciesParticles[species];
                    int NParticles = Particles.Length;
                    SpeciesParticleIDRanges.Add(species, (NParticlesOverall, NParticlesOverall + NParticles));
                    NParticlesOverall += NParticles;

                    int Size = species.HalfMap1Projector[GPUID].Dims.X;
                    int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                    int ParticleDiameterPix = (int)(species.DiameterAngstrom / (float)OptionsDataLoad.BinnedPixelSizeMean);

                    int[] RelevantSizes = GetRelevantImageSizes(SizeFull, BfactorWeightingThreshold).Select(v => Math.Min(Size, v)).ToArray();

                    #region Extract particle images

                    //Image AverageRealspace = new Image(new int3(SizeFull, SizeFull, NTilts), true, true);
                    Image AverageAmplitudes = new Image(new int3(SizeFull, SizeFull, NTilts), true);
                    //Image ImagesRealspace = new Image(new int3(SizeFull, SizeFull, NTilts));
                    Image ImagesAmplitudes = new Image(new int3(SizeFull, SizeFull, NTilts), true);

                    Image ExtractResult = new Image(new int3(SizeFull, SizeFull, NTilts));
                    Image ExtractResultFT = new Image(IntPtr.Zero, new int3(SizeFull, SizeFull, NTilts), true, true);
                    //Image ExtractResultFTCropped = new Image(IntPtr.Zero, new int3(Size, Size, NTilts), true, true);

                    int[] PlanForw = Helper.ArrayOfFunction(i => GPU.CreateFFTPlan(new int3(SizeFull, SizeFull, 1), (uint)NTilts), 1);

                    Helper.ForCPU(0, NParticles, 1, threadID => GPU.SetDevice(GPUID), (p, threadID) =>
                    {
                        GetImagesForOneParticle(OptionsDataLoad,
                                                TiltData,
                                                SizeFull,
                                                Particles[p].GetCoordinateSeries(DoseInterpolationSteps),
                                                PlanForw[threadID],
                                                ParticleDiameterPix,
                                                16,
                                                true,
                                                ExtractResult,
                                                ExtractResultFT);

                        //GPU.PadFT(ImagesFT.GetDevice(Intent.Read),
                        //          ExtractResultFTCropped.GetDevice(Intent.Write),
                        //          ImagesFT.Dims.Slice(),
                        //          ExtractResultFTCropped.Dims.Slice(),
                        //          (uint)NTilts);
                        //Image ImagesFTCropped = ImagesFT.AsPadded(new int2(Size));
                        //ImagesFT.Dispose();

                        GPU.Amplitudes(ExtractResultFT.GetDevice(Intent.Read),
                                       ImagesAmplitudes.GetDevice(Intent.Write),
                                       (uint)ExtractResultFT.ElementsComplex);
                        ImagesAmplitudes.Multiply(ImagesAmplitudes);
                        lock (AverageAmplitudes)
                            AverageAmplitudes.Add(ImagesAmplitudes);

                        //ImagesFTCropped.Multiply(Weights);

                        //lock (AverageRealspace)
                        //    AverageRealspace.Add(ExtractResultFT);

                        //ImagesFTCropped.Dispose();
                    }, null);

                    ExtractResult.Dispose();
                    ExtractResultFT.Dispose();
                    //ExtractResultFTCropped.Dispose();

                    ImagesAmplitudes.Dispose();

                    for (int i = 0; i < PlanForw.Length; i++)
                        GPU.DestroyFFTPlan(PlanForw[i]);

                    //AverageRealspace.Multiply(1f / NParticles);
                    //if (GPUID == 0)
                    //    AverageRealspace.AsIFFT().WriteMRC("d_avgreal.mrc", true);
                    //AverageRealspace.Dispose();

                    //ImagesRealspace.Dispose();

                    #endregion

                    #region Calculate spectra

                    //AverageRealspace.Multiply(1f / NParticles);
                    AverageAmplitudes.Multiply(1f / NParticles);
                    if (GPUID == 0)
                        AverageAmplitudes.WriteMRC($"d_avgamps_{species.Name}.mrc", true);

                    float[][] Amps2D = AverageAmplitudes.GetHost(Intent.Read);

                    for (int t = 0; t < NTilts; t++)
                    {
                        Helper.ForEachElementFT(new int2(SizeFull), (x, y, xx, yy, r, angle) =>
                        {
                            int idx = (int)Math.Round(r / (SizeFull / 2) * AverageSpectrum1DAll[t].Length);
                            if (idx < AverageSpectrum1DAll[t].Length)
                            {
                                AverageSpectrum1DAll[t][idx] += Amps2D[t][y * (SizeFull / 2 + 1) + x] * NParticles;
                                AverageSpectrum1DAllSamples[t][idx] += NParticles;
                            }
                        });
                    }

                    AverageAmplitudes.Dispose();

                    #endregion

                    #region Defoci and extraction positions

                    float[] Defoci = new float[NParticles * NTilts];
                    float2[] ExtractedAt = new float2[NParticles * NTilts];

                    for (int p = 0; p < NParticles; p++)
                    {
                        float3[] Positions = GetPositionInAllTilts(Particles[p].GetCoordinateSeries(DoseInterpolationSteps));
                        for (int t = 0; t < NTilts; t++)
                        {
                            Defoci[p * NTilts + t] = Positions[t].Z;
                            ExtractedAt[p * NTilts + t] = new float2(Positions[t].X, Positions[t].Y);
                        }
                    }

                    #endregion

                    #region Subset indices

                    int[] Subsets = Particles.Select(p => p.RandomSubset).ToArray();
                    IntPtr SubsetsPtr = GPU.MallocDeviceFromHostInt(Subsets, Subsets.Length);

                    #endregion

                    #region CTF superres factor

                    CTF MaxDefocusCTF = GetTiltCTF(IndicesSortedDose[0]);
                    int MinimumBoxSize = Math.Max(species.HalfMap1Projector[GPUID].Dims.X, MaxDefocusCTF.GetAliasingFreeSize(species.ResolutionRefinement));
                    float CTFSuperresFactor = (float)Math.Ceiling((float)MinimumBoxSize / species.HalfMap1Projector[GPUID].Dims.X);

                    #endregion

                    SpeciesParticleDefoci.Add(species, Defoci);
                    SpeciesParticleExtractedAt.Add(species, ExtractedAt);
                    SpeciesParticleSubsets.Add(species, SubsetsPtr);
                    SpeciesRefinementSize.Add(species, Size);
                    SpeciesRelevantRefinementSizes.Add(species, RelevantSizes);
                    SpeciesCTFSuperresFactor.Add(species, (int)CTFSuperresFactor);

                    species.HalfMap1Projector[GPUID].PutTexturesOnDevice();
                    species.HalfMap2Projector[GPUID].PutTexturesOnDevice();
                }

                #region Calculate 1D PS averaged over all species and particles

                for (int t = 0; t < NTilts; t++)
                {
                    for (int i = 0; i < AverageSpectrum1DAll[t].Length; i++)
                        AverageSpectrum1DAll[t][i] /= Math.Max(1, AverageSpectrum1DAllSamples[t][i]);

                    float SpectrumMean = MathHelper.Mean(AverageSpectrum1DAll[t]);
                    for (int i = 0; i < AverageSpectrum1DAll[t].Length; i++)
                        AverageSpectrum1DAll[t][i] /= SpectrumMean;

                    for (int i = 0; i < AverageSpectrum1DAll[t].Length; i++)
                        if (AverageSpectrum1DAll[t][i] <= 0)
                        {
                            for (int j = 0; j < AverageSpectrum1DAll[t].Length; j++)
                            {
                                if (i - j >= 0 && AverageSpectrum1DAll[t][i - j] > 0)
                                {
                                    AverageSpectrum1DAll[t][i] = AverageSpectrum1DAll[t][i - j];
                                    break;
                                }

                                if (i + j < AverageSpectrum1DAll[t].Length && AverageSpectrum1DAll[t][i + j] > 0)
                                {
                                    AverageSpectrum1DAll[t][i] = AverageSpectrum1DAll[t][i + j];
                                    break;
                                }
                            }
                        }

                    if (AverageSpectrum1DAll[t].Any(v => v <= 0))
                        throw new Exception("The 1D amplitude spectrum contains zeros, which it really shouldn't! Can't proceed.");
                }

                #endregion

                #region Calculate weights

                foreach (var species in allSpecies)
                {
                    if (SpeciesParticles[species].Length == 0)
                        continue;

                    Particle[] Particles = SpeciesParticles[species];
                    int NParticles = Particles.Length;

                    int Size = species.HalfMap1Projector[GPUID].Dims.X;
                    int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                    int ParticleDiameterPix = (int)(species.DiameterAngstrom / (float)OptionsDataLoad.BinnedPixelSizeMean);

                    int[] RelevantSizes = SpeciesRelevantRefinementSizes[species];

                    long ElementsSliceComplex = (Size / 2 + 1) * Size;

                    #region Dose weighting

                    ProcessingOptionsTomoSubReconstruction OptionsWeights = new ProcessingOptionsTomoSubReconstruction()
                    {
                        PixelSizeX = dataSource.PixelSizeX,
                        PixelSizeY = dataSource.PixelSizeY,
                        PixelSizeAngle = dataSource.PixelSizeAngle,

                        BinTimes = (decimal)Math.Log((species.ResolutionRefinement / 2) / (float)dataSource.PixelSizeMean, 2.0),

                        Dimensions = new float3((float)dataSource.DimensionsX,
                                                (float)dataSource.DimensionsY,
                                                (float)dataSource.DimensionsZ),

                        Invert = true,
                        NormalizeInput = true,
                        NormalizeOutput = false,

                        PrerotateParticles = true
                    };

                    Image CTFCoords = CTF.GetCTFCoords(Size, Size);
                    Image Weights = GetCTFsForOneParticle(OptionsWeights, VolumeDimensionsPhysical / 2, CTFCoords, null, true, true);
                    //Image VanillaWeights = Weights.GetCopy();
                    CTFCoords.Dispose();

                    #endregion

                    #region Divide weights by 1D PS, and create a 20 A high-passed version for CTF refinement

                    float[][] WeightsData = Weights.GetHost(Intent.ReadWrite);
                    for (int t = 0; t < NTilts; t++)
                    {
                        Helper.ForEachElementFT(new int2(Size), (x, y, xx, yy, r, angle) =>
                        {
                            if (r < Size / 2)
                            {
                                int idx = Math.Min(AverageSpectrum1DAll[t].Length - 1,
                                                   (int)Math.Round(r / (Size / 2) *
                                                                   (float)dataSource.PixelSizeMean /
                                                                   species.ResolutionRefinement *
                                                                   AverageSpectrum1DAll[t].Length));

                                WeightsData[t][y * (Size / 2 + 1) + x] /= AverageSpectrum1DAll[t][idx];
                            }
                            else
                            {
                                WeightsData[t][y * (Size / 2 + 1) + x] = 0;
                            }
                        });
                    }

                    //Weights.FreeDevice();
                    if (GPUID == 0)
                        Weights.WriteMRC($"d_weights_{species.Name}.mrc", true);

                    Image WeightsRelevantlySized = new Image(new int3(Size, Size, NTilts), true);
                    for (int t = 0; t < NTilts; t++)
                        GPU.CropFTRealValued(Weights.GetDeviceSlice(t, Intent.Read),
                                            WeightsRelevantlySized.GetDeviceSlice(t, Intent.Write),
                                            Weights.Dims.Slice(),
                                            new int3(RelevantSizes[t]).Slice(),
                                            1);
                    if (GPUID == 0)
                        WeightsRelevantlySized.WriteMRC($"d_weightsrelevant_{species.Name}.mrc", true);
                    Weights.Dispose();

                    Image CTFWeights = WeightsRelevantlySized.GetCopyGPU();
                    float[][] CTFWeightsData = CTFWeights.GetHost(Intent.ReadWrite);
                    for (int t = 0; t < CTFWeightsData.Length; t++)
                    {
                        int RelevantSize = RelevantSizes[t];
                        float R20 = Size * (species.ResolutionRefinement / 2 / 20f);
                        Helper.ForEachElementFT(new int2(RelevantSize), (x, y, xx, yy, r, angle) =>
                        {
                            float Weight = 1 - Math.Max(0, Math.Min(1, R20 - r));
                            CTFWeightsData[t][y * (RelevantSize / 2 + 1) + x] *= Weight;
                        });
                    }

                    CTFWeights.FreeDevice();
                    if (GPUID == 0)
                        CTFWeights.WriteMRC($"d_ctfweights_{species.Name}.mrc", true);

                    #endregion

                    SpeciesCTFWeights.Add(species, CTFWeights);
                    SpeciesTiltWeights.Add(species, WeightsRelevantlySized);
                }

                #endregion

                // Remove original tilt image data from device, and dispose masks
                for (int t = 0; t < NTilts; t++)
                {
                    if (TiltMasks != null)
                        TiltMasks[t]?.FreeDevice();
                    //TiltData[t].FreeDevice();
                }

                Console.WriteLine($"{GPU.GetFreeMemory(GPUID)} MB after spectra estimation of {Name}");

                #endregion

                #region Tilt movie refinement

                if (optionsMPA.DoTiltMovies)
                {
                    Console.WriteLine($"{GPU.GetFreeMemory(GPUID)} MB before movie refinement of {Name}");

                    Image StackForExport = null;
                    Image StackAverage = null;
                    Image AveragePlane = null;

                    for (int itilt = 0; itilt < NTilts; itilt++)
                    {
                        progressCallback($"Refining tilt movie {itilt + 1}/{NTilts}");

                        Movie tiltMovie = TiltMovies[itilt];
                        tiltMovie.NFrames = MapHeader.ReadFromFile(tiltMovie.Path).Dimensions.Z;

                        Image[] MovieData;
                        tiltMovie.LoadFrameData(OptionsDataLoad, gainRef, defectMap, out MovieData);

                        int3 StackDims = new int3(MovieData[0].Dims.X, MovieData[0].Dims.Y, MovieData.Length);
                        if (StackForExport == null || StackDims != StackForExport.Dims)
                        {
                            StackForExport?.Dispose();
                            StackForExport = new Image(IntPtr.Zero, StackDims);
                        }
                        for (int z = 0; z < MovieData.Length; z++)
                            GPU.CopyDeviceToDevice(MovieData[z].GetDevice(Intent.Read),
                                                   StackForExport.GetDeviceSlice(z, Intent.Write),
                                                   MovieData[z].ElementsReal);

                        if (StackAverage == null || StackAverage.Dims != StackForExport.Dims.Slice())
                        {
                            StackAverage?.Dispose();
                            StackAverage = new Image(IntPtr.Zero, StackForExport.Dims.Slice());
                            AveragePlane?.Dispose();
                            AveragePlane = new Image(IntPtr.Zero, StackForExport.Dims.Slice());
                        }
                        GPU.ReduceMean(StackForExport.GetDevice(Intent.Read),
                                       StackAverage.GetDevice(Intent.Write),
                                       (uint)StackAverage.ElementsReal,
                                       (uint)StackForExport.Dims.Z,
                                       1);
                        float[] AveragePlaneData = MathHelper.FitAndGeneratePlane(StackAverage.GetHost(Intent.Read)[0], new int2(StackAverage.Dims));
                        GPU.CopyHostToDevice(AveragePlaneData, AveragePlane.GetDevice(Intent.Write), AveragePlaneData.Length);

                        for (int z = 0; z < MovieData.Length; z++)
                        {
                            MovieData[z].Subtract(AveragePlane);
                            //MovieData[z].Bandpass(1f / LargestBox, 1f, false, 0f);

                            //MovieData[z].Multiply(-1f);
                            //MovieData[z].FreeDevice();
                        }

                        Dictionary<Species, Image> MovieSpeciesWeights = new Dictionary<Species, Image>();
                        foreach (var species in allSpecies)
                        {
                            if (!SpeciesParticles.ContainsKey(species))
                                continue;

                            Image Weights = new Image(IntPtr.Zero, new int3(SpeciesTiltWeights[species].Dims.X, SpeciesTiltWeights[species].Dims.Y, MovieData.Length), true);
                            for (int i = 0; i < MovieData.Length; i++)
                            {
                                GPU.CopyDeviceToDevice((species.ResolutionRefinement < 10 ? SpeciesCTFWeights : SpeciesTiltWeights)[species].GetDeviceSlice(itilt, Intent.Read),
                                                       Weights.GetDeviceSlice(i, Intent.Write),
                                                       Weights.ElementsSliceReal);
                            }
                            MovieSpeciesWeights.Add(species, Weights);
                        }

                        PerformMultiParticleRefinementOneTiltMovie(workingDirectory,
                                                                   optionsMPA,
                                                                   allSpecies,
                                                                   dataSource,
                                                                   tiltMovie,
                                                                   MovieData,
                                                                   itilt,
                                                                   SpeciesParticles,
                                                                   SpeciesParticleSubsets,
                                                                   SpeciesParticleIDRanges,
                                                                   SpeciesRefinementSize,
                                                                   SpeciesRelevantRefinementSizes,
                                                                   MovieSpeciesWeights,
                                                                   SpeciesCTFSuperresFactor);

                        foreach (var pair in MovieSpeciesWeights)
                            pair.Value.Dispose();

                        foreach (var frame in MovieData)
                            frame.Dispose();

                        tiltMovie.ExportMovie(StackForExport, tiltMovie.OptionsMovieExport);

                        tiltMovie.SaveMeta();
                    }

                    StackForExport.Dispose();
                    StackAverage.Dispose();
                    AveragePlane.Dispose();

                    for (int t = 0; t < NTilts; t++)
                        TiltData[t].FreeDevice();

                    LoadAndPreprocessTiltData();

                    for (int t = 0; t < NTilts; t++)
                    {
                        if (TiltMasks != null)
                            TiltMasks[t]?.FreeDevice();
                        //TiltData[t].FreeDevice();
                    }

                    Console.WriteLine($"{GPU.GetFreeMemory(GPUID)} MB after movie refinement of {Name}");
                }

                #endregion

                #region Allocate pinned host memory for extracted particle images

                foreach (var species in allSpecies)
                {
                    int NParticles = SpeciesParticles[species].Length;
                    if (NParticles == 0)
                        continue;

                    int Size = species.HalfMap1Projector[GPUID].Dims.X;
                    int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                    int[] RelevantSizes = SpeciesRelevantRefinementSizes[species];

                    IntPtr[] ImagesFTPinned = Helper.ArrayOfFunction(t => GPU.MallocHostPinned((new int3(RelevantSizes[t]).Slice().ElementsFFT()) * 2 * (long)NParticles), NTilts);
                    IntPtr[] ImagesFTQPinned = null;
                    if (species.DoEwald)
                        ImagesFTQPinned = Helper.ArrayOfFunction(t => GPU.MallocDevice((new int3(RelevantSizes[t]).Slice().ElementsFFT()) * 2 * (long)NParticles), NTilts);
                    GPU.CheckGPUExceptions();

                    SpeciesParticleImages.Add(species, ImagesFTPinned);
                    if (species.DoEwald)
                        SpeciesParticleQImages.Add(species, ImagesFTQPinned);
                }

                #endregion

                #region Helper functions

                Action<bool> ReextractPaddedParticles = (CorrectBeamTilt) =>
                {
                    float AngPixExtract = (float)OptionsDataLoad.BinnedPixelSizeMean;
                    int BatchSize = optionsMPA.BatchSize;

                    foreach (var species in allSpecies)
                    {
                        if (!SpeciesParticles.ContainsKey(species))
                            continue;

                        Particle[] Particles = SpeciesParticles[species];
                        int NParticles = Particles.Length;

                        int SizeRefine = species.HalfMap1Projector[GPUID].Dims.X;
                        int SizeRefineSuper = SizeRefine * SpeciesCTFSuperresFactor[species];
                        int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                        int SizeFullSuper = SizeFull * SpeciesCTFSuperresFactor[species];
                        int[] SizesRelevant = SpeciesRelevantRefinementSizes[species];

                        float AngPixRefine = species.ResolutionRefinement / 2;
                        int ParticleDiameterPix = (int)(species.DiameterAngstrom / AngPixRefine);

                        float2[] ExtractedAt = SpeciesParticleExtractedAt[species];

                        Image CoordsCTF = CTF.GetCTFCoords(SizeRefineSuper, SizeRefineSuper);
                        Image PhaseCorrection = CTF.GetPhaseCorrection(AngPixRefine, SizeRefineSuper);
                        Image GammaCorrection = CTF.GetGammaCorrection(AngPixRefine, SizeRefineSuper);

                        Image Extracted = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                        Image ExtractedFT = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                        Image ExtractedCropped = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize));
                        Image ExtractedCroppedFT = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                        Image ExtractedCroppedFTRelevantSize = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                        Image ExtractedCTF = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true, true);

                        //Image Average = new Image(new int3(SizeRefine, SizeRefine, BatchSize));

                        int PlanForwSuper = GPU.CreateFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                        int PlanBackSuper = GPU.CreateIFFTPlan(new int3(SizeRefineSuper, SizeRefineSuper, 1), (uint)BatchSize);
                        int PlanForw = GPU.CreateFFTPlan(new int3(SizeRefine, SizeRefine, 1), (uint)BatchSize);

                        if (PlanForwSuper <= 0 || PlanBackSuper <= 0 || PlanForw <= 0)
                        {
                            Console.WriteLine($"SizeFullSuper = {SizeFullSuper}, BatchSize = {BatchSize}, free memory = {GPU.GetFreeMemory(GPUID)}");
                            throw new Exception("No FFT plans created!");
                        }

                        bool[] PQReverse = { species.EwaldReverse, !species.EwaldReverse };
                        IntPtr[][] PQStorage = species.DoEwald ? new[] { SpeciesParticleImages[species], SpeciesParticleQImages[species] } :
                                                                 new[] { SpeciesParticleImages[species] };
                        
                        for (int iewald = 0; iewald < (species.DoEwald ? 2 : 1); iewald++)
                        {
                            for (int batchStart = 0; batchStart < NParticles; batchStart += BatchSize)
                            {
                                int CurBatch = Math.Min(BatchSize, NParticles - batchStart);
                                IEnumerable<Particle> BatchParticles = Particles.Skip(batchStart).Take(CurBatch);
                                float3[] CoordinatesMoving = Helper.Combine(BatchParticles.Select(p => p.GetCoordinateSeries(DoseInterpolationSteps)));
                                float3[] AnglesMoving = Helper.Combine(BatchParticles.Select(p => p.GetAngleSeries(DoseInterpolationSteps)));

                                for (int t = 0; t < NTilts; t++)
                                {
                                    float3[] CoordinatesTilt = new float3[CurBatch];
                                    for (int p = 0; p < CurBatch; p++)
                                        CoordinatesTilt[p] = CoordinatesMoving[p * NTilts + t];

                                    float3[] ImageCoords = GetPositionsInOneTilt(CoordinatesTilt, t);

                                    float[] Defoci = new float[CurBatch];
                                    int3[] ExtractOrigins = new int3[CurBatch];
                                    float3[] ResidualShifts = new float3[BatchSize];
                                    for (int p = 0; p < CurBatch; p++)
                                    {
                                        float ScaledX = ImageCoords[p].X / AngPixExtract, ScaledY = ImageCoords[p].Y / AngPixExtract;
                                        ExtractOrigins[p] = new int3((int)ScaledX - SizeFullSuper / 2, (int)ScaledY - SizeFullSuper / 2, 0);
                                        ResidualShifts[p] = -new float3(ScaledX - (int)ScaledX - SizeFullSuper / 2, ScaledY - (int)ScaledY - SizeFullSuper / 2, 0);
                                        Defoci[p] = ImageCoords[p].Z;
                                        ExtractedAt[(batchStart + p) * NTilts + t] = new float2(ImageCoords[p]);
                                    }

                                    GPU.Extract(TiltData[t].GetDevice(Intent.Read),
                                                Extracted.GetDevice(Intent.Write),
                                                TiltData[t].Dims.Slice(),
                                                new int3(SizeFullSuper, SizeFullSuper, 1),
                                                Helper.ToInterleaved(ExtractOrigins),
                                                (uint)CurBatch);

                                    GPU.FFT(Extracted.GetDevice(Intent.Read),
                                            ExtractedFT.GetDevice(Intent.Write),
                                            new int3(SizeFullSuper, SizeFullSuper, 1),
                                            (uint)CurBatch,
                                            PlanForwSuper);

                                    ExtractedFT.ShiftSlices(ResidualShifts);
                                    ExtractedFT.Multiply(1f / (SizeFullSuper * SizeFullSuper));

                                    GPU.CropFT(ExtractedFT.GetDevice(Intent.Read),
                                               Extracted.GetDevice(Intent.Write),
                                               new int3(SizeFullSuper, SizeFullSuper, 1),
                                               new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                               (uint)CurBatch);

                                    if (CorrectBeamTilt)
                                        GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                           PhaseCorrection.GetDevice(Intent.Read),
                                                                           Extracted.GetDevice(Intent.Write),
                                                                           PhaseCorrection.ElementsSliceComplex,
                                                                           (uint)CurBatch);

                                    if (species.DoEwald)
                                    {
                                        GetComplexCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, PQReverse[iewald], ExtractedCTF, true);

                                        GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                          ExtractedCTF.GetDevice(Intent.Read),
                                                                          ExtractedFT.GetDevice(Intent.Write),
                                                                          ExtractedCTF.ElementsComplex,
                                                                          1);
                                    }
                                    else
                                    {
                                        GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, ExtractedCTF, true);

                                        GPU.MultiplyComplexSlicesByScalar(Extracted.GetDevice(Intent.Read),
                                                                          ExtractedCTF.GetDevice(Intent.Read),
                                                                          ExtractedFT.GetDevice(Intent.Write),
                                                                          ExtractedCTF.ElementsComplex,
                                                                          1);
                                    }

                                    GPU.IFFT(ExtractedFT.GetDevice(Intent.Read),
                                             Extracted.GetDevice(Intent.Write),
                                             new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                             (uint)CurBatch,
                                             PlanBackSuper,
                                             false);

                                    GPU.CropFTFull(Extracted.GetDevice(Intent.Read),
                                                    ExtractedCropped.GetDevice(Intent.Write),
                                                    new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                    new int3(SizeRefine, SizeRefine, 1),
                                                    (uint)CurBatch);

                                    GPU.SphereMask(ExtractedCropped.GetDevice(Intent.Read),
                                                   ExtractedCropped.GetDevice(Intent.Write),
                                                   ExtractedCropped.Dims.Slice(),
                                                   ParticleDiameterPix / 2f,
                                                   16 * AngPixExtract / AngPixRefine,
                                                   true,
                                                   (uint)CurBatch);

                                    //Average.Add(ExtractedCropped);

                                    GPU.FFT(ExtractedCropped.GetDevice(Intent.Read),
                                            ExtractedCroppedFT.GetDevice(Intent.Write),
                                            new int3(SizeRefine, SizeRefine, 1),
                                            (uint)CurBatch,
                                            PlanForw);

                                    ExtractedCroppedFT.Multiply(1f / (SizeRefine * SizeRefine));

                                    GPU.CropFT(ExtractedCroppedFT.GetDevice(Intent.Read),
                                               ExtractedCroppedFTRelevantSize.GetDevice(Intent.Write),
                                               new int3(SizeRefine).Slice(),
                                               new int3(SizesRelevant[t]).Slice(),
                                               (uint)CurBatch);

                                    GPU.CopyDeviceToHostPinned(ExtractedCroppedFTRelevantSize.GetDevice(Intent.Read),
                                                               new IntPtr((long)PQStorage[iewald][t] + (new int3(SizesRelevant[t]).Slice().ElementsFFT()) * 2 * batchStart * sizeof(float)),
                                                               (new int3(SizesRelevant[t]).Slice().ElementsFFT()) * 2 * CurBatch);
                                }
                            }
                        }

                        //Average.WriteMRC("d_average.mrc", true);
                        //Average.Dispose();

                        CoordsCTF.Dispose();
                        PhaseCorrection.Dispose();
                        GammaCorrection.Dispose();
                        Extracted.Dispose();
                        ExtractedFT.Dispose();
                        ExtractedCropped.Dispose();
                        ExtractedCroppedFT.Dispose();
                        ExtractedCroppedFTRelevantSize.Dispose();
                        ExtractedCTF.Dispose();

                        GPU.DestroyFFTPlan(PlanForwSuper);
                        GPU.DestroyFFTPlan(PlanBackSuper);
                        GPU.DestroyFFTPlan(PlanForw);
                    }

                    //foreach (var image in TiltData)
                    //    image.FreeDevice();

                    GPU.CheckGPUExceptions();
                };

                Func<float2[]> GetRawShifts = () =>
                {
                    float2[] Result = new float2[NParticlesOverall * NTilts];

                    foreach (var species in allSpecies)
                    {
                        Particle[] Particles = SpeciesParticles[species];
                        int NParticles = Particles.Length;
                        float SpeciesAngPix = species.ResolutionRefinement / 2;
                        if (NParticles == 0)
                            continue;

                        int Offset = SpeciesParticleIDRanges[species].Start;

                        float3[] ParticlePositions = new float3[NParticles * NTilts];
                        for (int p = 0; p < NParticles; p++)
                        {
                            float3[] Positions = Particles[p].GetCoordinateSeries(DoseInterpolationSteps);

                            for (int t = 0; t < NTilts; t++)
                                ParticlePositions[p * NTilts + t] = Positions[t];
                        }

                        float3[] ParticlePositionsProjected = GetPositionInAllTilts(ParticlePositions);
                        float2[] ParticleExtractedAt = SpeciesParticleExtractedAt[species];

                        for (int p = 0; p < NParticles; p++)
                            for (int t = 0; t < NTilts; t++)
                                Result[(Offset + p) * NTilts + t] = (new float2(ParticlePositionsProjected[p * NTilts + t]) - ParticleExtractedAt[p * NTilts + t]);
                    }

                    return Result;
                };

                Func<float2, Species, float[]> GetRawCCSpecies = (shiftBias, Species) =>
                {
                    Particle[] Particles = SpeciesParticles[Species];

                    int NParticles = Particles.Length;
                    float AngPixRefine = Species.ResolutionRefinement / 2;

                    float[] SpeciesResult = new float[NParticles * NTilts * 3];
                    if (NParticles == 0)
                        return SpeciesResult;

                    float[] SpeciesResultQ = new float[NParticles * NTilts * 3];

                    float3[] ParticlePositions = new float3[NParticles * NTilts];
                    float3[] ParticleAngles = new float3[NParticles * NTilts];
                    for (int p = 0; p < NParticles; p++)
                    {
                        float3[] Positions = Particles[p].GetCoordinateSeries(DoseInterpolationSteps);
                        float3[] Angles = Particles[p].GetAngleSeries(DoseInterpolationSteps);

                        for (int t = 0; t < NTilts; t++)
                        {
                            ParticlePositions[p * NTilts + t] = Positions[t];
                            ParticleAngles[p * NTilts + t] = Angles[t];
                        }
                    }

                    float3[] ParticlePositionsProjected = GetPositionInAllTilts(ParticlePositions);
                    float3[] ParticleAnglesInTilts = GetParticleAngleInAllTilts(ParticlePositions, ParticleAngles);

                    float2[] ParticleExtractedAt = SpeciesParticleExtractedAt[Species];
                    float2[] ParticleShifts = new float2[NTilts * NParticles];
                    for (int p = 0; p < NParticles; p++)
                        for (int t = 0; t < NTilts; t++)
                            ParticleShifts[p * NTilts + t] = (new float2(ParticlePositionsProjected[p * NTilts + t]) - ParticleExtractedAt[p * NTilts + t] + shiftBias) / AngPixRefine;

                    int[] RelevantSizes = SpeciesRelevantRefinementSizes[Species];

                    int SizeRefine = Species.HalfMap1Projector[GPUID].Dims.X;
                    int SizeFull = Species.HalfMap1Reconstruction[GPUID].Dims.X;

                    Image PhaseCorrection = CTF.GetPhaseCorrection(AngPixRefine, SizeRefine);
                    Image PhaseCorrectionAll = new Image(new int3(SizeRefine, SizeRefine, NTilts), true, true);
                    for (int t = 0; t < NTilts; t++)
                        GPU.CropFT(PhaseCorrection.GetDevice(Intent.Read),
                                    PhaseCorrectionAll.GetDeviceSlice(t, Intent.Write),
                                    PhaseCorrection.Dims.Slice(),
                                    new int3(RelevantSizes[t]).Slice(),
                                    1);

                    GPU.MultiParticleDiff(SpeciesResult,
                                            SpeciesParticleImages[Species],
                                            SizeRefine,
                                            RelevantSizes,
                                            Helper.ToInterleaved(ParticleShifts),
                                            Helper.ToInterleaved(ParticleAnglesInTilts),
                                            MagnificationCorrection,
                                            (Species.ResolutionRefinement < 8 ? SpeciesCTFWeights : SpeciesTiltWeights)[Species].GetDevice(Intent.Read),
                                            PhaseCorrectionAll.GetDevice(Intent.Read),
                                            Species.DoEwald ? CTF.GetEwaldRadius(SizeFull, (float)Species.PixelSize) : 0,
                                            Species.CurrentMaxShellRefinement,
                                            new[] { Species.HalfMap1Projector[GPUID].t_DataRe, Species.HalfMap2Projector[GPUID].t_DataRe },
                                            new[] { Species.HalfMap1Projector[GPUID].t_DataIm, Species.HalfMap2Projector[GPUID].t_DataIm },
                                            Species.HalfMap1Projector[GPUID].Oversampling,
                                            Species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                            SpeciesParticleSubsets[Species],
                                            NParticles,
                                            NTilts);

                    if (Species.DoEwald)
                        GPU.MultiParticleDiff(SpeciesResultQ,
                                                SpeciesParticleQImages[Species],
                                                SizeRefine,
                                                RelevantSizes,
                                                Helper.ToInterleaved(ParticleShifts),
                                                Helper.ToInterleaved(ParticleAnglesInTilts),
                                                MagnificationCorrection,
                                                (Species.ResolutionRefinement < 8 ? SpeciesCTFWeights : SpeciesTiltWeights)[Species].GetDevice(Intent.Read),
                                                PhaseCorrectionAll.GetDevice(Intent.Read),
                                                -CTF.GetEwaldRadius(SizeFull, (float)Species.PixelSize),
                                                Species.CurrentMaxShellRefinement,
                                                new[] { Species.HalfMap1Projector[GPUID].t_DataRe, Species.HalfMap2Projector[GPUID].t_DataRe },
                                                new[] { Species.HalfMap1Projector[GPUID].t_DataIm, Species.HalfMap2Projector[GPUID].t_DataIm },
                                                Species.HalfMap1Projector[GPUID].Oversampling,
                                                Species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                                SpeciesParticleSubsets[Species],
                                                NParticles,
                                                NTilts);

                    PhaseCorrection.Dispose();
                    PhaseCorrectionAll.Dispose();

                    if (Species.DoEwald)
                        for (int i = 0; i < SpeciesResult.Length; i++)
                            SpeciesResult[i] += SpeciesResultQ[i];

                    return SpeciesResult;
                };

                Func<float2, float[]> GetRawCC = (shiftBias) =>
                {
                    float[] Result = new float[NParticlesOverall * NTilts * 3];

                    for (int ispecies = 0; ispecies < allSpecies.Length; ispecies++)
                    {
                        Species Species = allSpecies[ispecies];
                        Particle[] Particles = SpeciesParticles[Species];

                        int NParticles = Particles.Length;
                        if (NParticles == 0)
                            continue;

                        float[] SpeciesResult = GetRawCCSpecies(shiftBias, Species);

                        int Offset = SpeciesParticleIDRanges[Species].Start * NTilts * 3;
                        Array.Copy(SpeciesResult, 0, Result, Offset, SpeciesResult.Length);
                    }

                    GPU.CheckGPUExceptions();
                    //Console.WriteLine(GPU.GetFreeMemory(GPUID));

                    return Result;
                };

                Func<double[]> GetPerTiltCC = () =>
                {
                    double[] Result = new double[NTilts * 3];
                    float[] RawResult = GetRawCC(new float2(0));

                    for (int p = 0; p < NParticlesOverall; p++)
                        for (int t = 0; t < NTilts; t++)
                        {
                            Result[t * 3 + 0] += RawResult[(p * NTilts + t) * 3 + 0];
                            Result[t * 3 + 1] += RawResult[(p * NTilts + t) * 3 + 1];
                            Result[t * 3 + 2] += RawResult[(p * NTilts + t) * 3 + 2];
                        }

                    Result = Helper.ArrayOfFunction(t => Result[t * 3 + 0] / Math.Max(1e-10, Math.Sqrt(Result[t * 3 + 1] * Result[t * 3 + 2])) * 100 * NParticlesOverall, NTilts);

                    return Result;
                };

                Func<double[]> GetPerParticleCC = () =>
                {
                    double[] Result = new double[NParticlesOverall * 3];
                    float[] RawResult = GetRawCC(new float2(0));

                    for (int p = 0; p < NParticlesOverall; p++)
                        for (int t = 0; t < NTilts; t++)
                        {
                            Result[p * 3 + 0] += RawResult[(p * NTilts + t) * 3 + 0];
                            Result[p * 3 + 1] += RawResult[(p * NTilts + t) * 3 + 1];
                            Result[p * 3 + 2] += RawResult[(p * NTilts + t) * 3 + 2];
                        }

                    Result = Helper.ArrayOfFunction(p => Result[p * 3 + 0] / Math.Max(1e-10, Math.Sqrt(Result[p * 3 + 1] * Result[p * 3 + 2])) * 100 * NTilts, NParticlesOverall);

                    return Result;
                };

                Func<Species, double[]> GetPerParticleCCSpecies = (species) =>
                {
                    Particle[] Particles = SpeciesParticles[species];
                    int NParticles = Particles.Length;

                    double[] Result = new double[NParticles * 3];
                    float[] RawResult = GetRawCCSpecies(new float2(0), species);

                    for (int p = 0; p < NParticles; p++)
                        for (int t = 0; t < NTilts; t++)
                        {
                            Result[p * 3 + 0] += RawResult[(p * NTilts + t) * 3 + 0];
                            Result[p * 3 + 1] += RawResult[(p * NTilts + t) * 3 + 1];
                            Result[p * 3 + 2] += RawResult[(p * NTilts + t) * 3 + 2];
                        }

                    Result = Helper.ArrayOfFunction(p => Result[p * 3 + 0] /
                                                         Math.Max(1e-10, Math.Sqrt(Result[p * 3 + 1] * Result[p * 3 + 2])) *
                                                         100 * NTilts, NParticles);

                    return Result;
                };

                Func<(float[] xp, float[] xm, float[] yp, float[] ym, float delta2)> GetRawShiftGradients = () =>
                {
                    float Delta = 0.1f;
                    float Delta2 = Delta * 2;

                    float[] h_ScoresXP = GetRawCC(float2.UnitX * Delta);
                    float[] h_ScoresXM = GetRawCC(-float2.UnitX * Delta);
                    float[] h_ScoresYP = GetRawCC(float2.UnitY * Delta);
                    float[] h_ScoresYM = GetRawCC(-float2.UnitY * Delta);

                    //for (int i = 0; i < Result.Length; i++)
                    //    Result[i] = new float2((h_ScoresXP[i] - h_ScoresXM[i]) / Delta2 * 100,
                    //                           (h_ScoresYP[i] - h_ScoresYM[i]) / Delta2 * 100);

                    return (h_ScoresXP, h_ScoresXM, h_ScoresYP, h_ScoresYM, Delta2);
                };

                #endregion

                #region BFGS prerequisites

                float2[] OriginalImageWarps = Helper.ArrayOfFunction(t => new float2(GridMovementX.Values[t], GridMovementY.Values[t]), GridMovementX.Values.Length);
                float3[] OriginalVolumeWarps = Helper.ArrayOfFunction(t => new float3(GridVolumeWarpX.Values[t], GridVolumeWarpY.Values[t], GridVolumeWarpZ.Values[t]), GridVolumeWarpX.Values.Length);

                float[] OriginalAngleX = GridAngleX.Values.ToArray();
                float[] OriginalAngleY = GridAngleY.Values.ToArray();
                float[] OriginalAngleZ = GridAngleZ.Values.ToArray();

                float4[] OriginalTiltCTFs = Helper.ArrayOfFunction(t => new float4(GridCTFDefocus.Values[t],
                                                                                   GridCTFDefocusDelta.Values[t],
                                                                                   GridCTFDefocusAngle.Values[t],
                                                                                   GridCTFPhase.Values[t]), NTilts);

                float[] OriginalParamsCTF =
                {
                    (float)CTF.Cs,
                };

                CTFOptimizationTypes[] CTFStepTypes =
                {
                    CTFOptimizationTypes.Defocus,
                    CTFOptimizationTypes.AstigmatismDelta,
                    CTFOptimizationTypes.AstigmatismAngle,
                    CTFOptimizationTypes.PhaseShift,
                    CTFOptimizationTypes.Zernike2,
                    CTFOptimizationTypes.Zernike2,
                    CTFOptimizationTypes.Zernike2,
                    CTFOptimizationTypes.Zernike4,
                    CTFOptimizationTypes.Zernike4,
                    CTFOptimizationTypes.Zernike4,
                    CTFOptimizationTypes.Zernike4,
                    CTFOptimizationTypes.Zernike4,
                    CTFOptimizationTypes.Cs,
                };

                float[] OriginalZernikeOdd = CTF.ZernikeCoeffsOdd.ToList().ToArray();
                float[] OriginalZernikeEven = CTF.ZernikeCoeffsEven.ToList().ToArray();

                //float2 OriginalBeamTilt = CTF.BeamTilt;
                float3 OriginalMagnification = MagnificationCorrection;

                float3[][] OriginalParticlePositions = allSpecies.Select(s => Helper.Combine(SpeciesParticles[s].Select(p => p.Coordinates))).ToArray();
                float3[][] OriginalParticleAngles = allSpecies.Select(s => Helper.Combine(SpeciesParticles[s].Select(p => p.Angles))).ToArray();

                int BFGSIterations = 0;
                WarpOptimizationTypes CurrentOptimizationTypeWarp = 0;
                CTFOptimizationTypes CurrentOptimizationTypeCTF = 0;

                double[] InitialParametersWarp = new double[GridMovementX.Values.Length * 2 +
                                                            GridVolumeWarpX.Values.Length * 3 +
                                                            GridAngleX.Values.Length * 3 +
                                                            OriginalParticlePositions.Select(a => a.Length).Sum() * 3 +
                                                            OriginalParticleAngles.Select(a => a.Length).Sum() * 3 +
                                                            CTF.ZernikeCoeffsOdd.Length +
                                                            3];
                double[] InitialParametersDefocus = new double[NTilts * 4 +
                                                               CTF.ZernikeCoeffsEven.Length +
                                                               OriginalParamsCTF.Length];

                #endregion

                #region Set parameters from vector

                Action<double[], TiltSeries, bool> SetWarpFromVector = (input, series, setParticles) =>
                {
                    int Offset = 0;

                    float[] MovementXData = new float[GridMovementX.Values.Length];
                    float[] MovementYData = new float[GridMovementX.Values.Length];
                    for (int i = 0; i < MovementXData.Length; i++)
                    {
                        MovementXData[i] = OriginalImageWarps[i].X + (float)input[Offset + i];
                        MovementYData[i] = OriginalImageWarps[i].Y + (float)input[Offset + MovementXData.Length + i];
                    }
                    series.GridMovementX = new CubicGrid(GridMovementX.Dimensions, MovementXData);
                    series.GridMovementY = new CubicGrid(GridMovementY.Dimensions, MovementYData);

                    Offset += MovementXData.Length * 2;

                    float[] VolumeXData = new float[GridVolumeWarpX.Values.Length];
                    float[] VolumeYData = new float[GridVolumeWarpX.Values.Length];
                    float[] VolumeZData = new float[GridVolumeWarpX.Values.Length];
                    for (int i = 0; i < VolumeXData.Length; i++)
                    {
                        VolumeXData[i] = OriginalVolumeWarps[i].X + (float)input[Offset + i];
                        VolumeYData[i] = OriginalVolumeWarps[i].Y + (float)input[Offset + VolumeXData.Length + i];
                        VolumeZData[i] = OriginalVolumeWarps[i].Z + (float)input[Offset + VolumeXData.Length + VolumeYData.Length + i];
                    }
                    series.GridVolumeWarpX = new LinearGrid4D(GridVolumeWarpX.Dimensions, VolumeXData);
                    series.GridVolumeWarpY = new LinearGrid4D(GridVolumeWarpY.Dimensions, VolumeYData);
                    series.GridVolumeWarpZ = new LinearGrid4D(GridVolumeWarpZ.Dimensions, VolumeZData);

                    Offset += VolumeXData.Length * 3;

                    float[] AngleXData = new float[GridAngleX.Values.Length];
                    float[] AngleYData = new float[GridAngleY.Values.Length];
                    float[] AngleZData = new float[GridAngleZ.Values.Length];
                    for (int i = 0; i < AngleXData.Length; i++)
                    {
                        AngleXData[i] = OriginalAngleX[i] + (float)input[Offset + i];
                        AngleYData[i] = OriginalAngleY[i] + (float)input[Offset + AngleXData.Length + i];
                        AngleZData[i] = OriginalAngleZ[i] + (float)input[Offset + AngleXData.Length * 2 + i];
                    }
                    series.GridAngleX = new CubicGrid(GridAngleX.Dimensions, AngleXData);
                    series.GridAngleY = new CubicGrid(GridAngleY.Dimensions, AngleYData);
                    series.GridAngleZ = new CubicGrid(GridAngleZ.Dimensions, AngleZData);

                    Offset += AngleXData.Length * 3;

                    if (setParticles)
                    {
                        for (int ispecies = 0; ispecies < allSpecies.Length; ispecies++)
                        {
                            Particle[] Particles = SpeciesParticles[allSpecies[ispecies]];

                            int ResCoords = allSpecies[ispecies].TemporalResolutionMovement;

                            for (int p = 0; p < Particles.Length; p++)
                            {
                                for (int ic = 0; ic < ResCoords; ic++)
                                {
                                    Particles[p].Coordinates[ic] = OriginalParticlePositions[ispecies][p * ResCoords + ic] + new float3((float)input[Offset + (p * 6 + 0) * ResCoords + ic],
                                                                                                                                        (float)input[Offset + (p * 6 + 1) * ResCoords + ic],
                                                                                                                                        (float)input[Offset + (p * 6 + 2) * ResCoords + ic]);
                                    Particles[p].Angles[ic] = OriginalParticleAngles[ispecies][p * ResCoords + ic] + new float3((float)input[Offset + (p * 6 + 3) * ResCoords + ic],
                                                                                                                                (float)input[Offset + (p * 6 + 4) * ResCoords + ic],
                                                                                                                                (float)input[Offset + (p * 6 + 5) * ResCoords + ic]);
                                }
                            }

                            Offset += OriginalParticlePositions[ispecies].Length * 6;
                        }
                    }
                    else
                    {
                        Offset += OriginalParticlePositions.Select(a => a.Length).Sum() * 6;
                    }

                    //CTF.BeamTilt = OriginalBeamTilt + new float2((float)input[input.Length - 5],
                    //                                             (float)input[input.Length - 4]);

                    for (int icoeff = 0; icoeff < CTF.ZernikeCoeffsOdd.Length; icoeff++)
                        CTF.ZernikeCoeffsOdd[icoeff] = OriginalZernikeOdd[icoeff] + (float)input[Offset + icoeff];

                    Offset += CTF.ZernikeCoeffsOdd.Length;

                    MagnificationCorrection = OriginalMagnification + new float3((float)input[input.Length - 3] / 100,
                                                                                 (float)input[input.Length - 2] / 100,
                                                                                 (float)input[input.Length - 1]);

                    // MagnificationCorrection follows a different, weird convention.
                    // .x and .y define the X and Y axes of a scaling matrix, rotated by -.z
                    // Scaling .x up means the pixel size along that axis is smaller, thus a negative DeltaPercent
                    CTF.PixelSizeDeltaPercent = -(decimal)(MagnificationCorrection.X - (MagnificationCorrection.X + MagnificationCorrection.Y) / 2);
                    CTF.PixelSizeAngle = (decimal)(-MagnificationCorrection.Z * Helper.ToDeg);
                };

                Action<double[]> SetDefocusFromVector = input =>
                {
                    int Offset = 0;

                    float[] DefocusValues = Helper.ArrayOfFunction(t => OriginalTiltCTFs[t].X + (float)input[t * 4 + 0] * 0.1f, NTilts);
                    float[] AstigmatismValues = Helper.ArrayOfFunction(t => OriginalTiltCTFs[t].Y + (float)input[t * 4 + 1] * 0.1f, NTilts);
                    float[] AngleValues = Helper.ArrayOfFunction(t => OriginalTiltCTFs[t].Z + (float)input[t * 4 + 2] * 36, NTilts);
                    float[] PhaseValues = Helper.ArrayOfFunction(t => OriginalTiltCTFs[t].W + (float)input[t * 4 + 3] * 36, NTilts);

                    GridCTFDefocus = new CubicGrid(new int3(1, 1, NTilts), DefocusValues);
                    GridCTFDefocusDelta = new CubicGrid(new int3(1, 1, NTilts), AstigmatismValues);
                    GridCTFDefocusAngle = new CubicGrid(new int3(1, 1, NTilts), AngleValues);
                    GridCTFPhase = new CubicGrid(new int3(1, 1, NTilts), PhaseValues);

                    Offset += NTilts * 4;

                    {
                        float[] ValuesZernike = new float[CTF.ZernikeCoeffsEven.Length];
                        for (int i = 0; i < ValuesZernike.Length; i++)
                            ValuesZernike[i] = OriginalZernikeEven[i] + (float)input[Offset + i];

                        CTF.ZernikeCoeffsEven = ValuesZernike;
                        Offset += CTF.ZernikeCoeffsEven.Length;
                    }

                    CTF.Cs = (decimal)(OriginalParamsCTF[0] + input[input.Length - 1]);
                    //CTF.PixelSizeDeltaPercent = (decimal)(OriginalParamsCTF[1] + input[input.Length - 2] * 0.1f);
                    //CTF.PixelSizeAngle = (decimal)(OriginalParamsCTF[2] + input[input.Length - 1] * 36);
                };

                #endregion

                #region Wiggle weights

                progressCallback("Precomputing gradient weights...");

                int NWiggleDifferentiable = GridMovementX.Values.Length +
                                            GridMovementY.Values.Length +
                                            GridVolumeWarpX.Values.Length +
                                            GridVolumeWarpY.Values.Length +
                                            GridVolumeWarpZ.Values.Length;
                (int[] indices, float2[] weights)[] AllWiggleWeights = new (int[] indices, float2[] weights)[NWiggleDifferentiable];

                if (optionsMPA.DoImageWarp || optionsMPA.DoVolumeWarp)
                {
                    TiltSeries[] ParallelSeriesCopies = Helper.ArrayOfFunction(i => new TiltSeries(this.Path), 16);

                    Helper.ForCPU(0, NWiggleDifferentiable, ParallelSeriesCopies.Length, (threadID) =>
                    {
                        ParallelSeriesCopies[threadID].VolumeDimensionsPhysical = VolumeDimensionsPhysical;
                        ParallelSeriesCopies[threadID].ImageDimensionsPhysical = ImageDimensionsPhysical;
                    },
                    (iwiggle, threadID) =>
                    {
                        double[] WiggleParams = new double[InitialParametersWarp.Length];
                        WiggleParams[iwiggle] = 1;
                        SetWarpFromVector(WiggleParams, ParallelSeriesCopies[threadID], false);

                        float2[] RawShifts = new float2[NParticlesOverall * NTilts];
                        foreach (var species in allSpecies)
                        {
                            Particle[] Particles = SpeciesParticles[species];
                            int NParticles = Particles.Length;
                            float SpeciesAngPix = species.ResolutionRefinement / 2;
                            if (NParticles == 0)
                                continue;

                            int Offset = SpeciesParticleIDRanges[species].Start;

                            float3[] ParticlePositions = new float3[NParticles * NTilts];
                            for (int p = 0; p < NParticles; p++)
                            {
                                float3[] Positions = Particles[p].GetCoordinateSeries(DoseInterpolationSteps);

                                for (int t = 0; t < NTilts; t++)
                                    ParticlePositions[p * NTilts + t] = Positions[t];
                            }

                            float3[] ParticlePositionsProjected = ParallelSeriesCopies[threadID].GetPositionInAllTilts(ParticlePositions);
                            float2[] ParticleExtractedAt = SpeciesParticleExtractedAt[species];

                            for (int p = 0; p < NParticles; p++)
                                for (int t = 0; t < NTilts; t++)
                                    RawShifts[(Offset + p) * NTilts + t] = (new float2(ParticlePositionsProjected[p * NTilts + t]) - ParticleExtractedAt[p * NTilts + t]);
                        }

                        List<int> Indices = new List<int>();
                        List<float2> Weights = new List<float2>();
                        for (int i = 0; i < RawShifts.Length; i++)
                        {
                            if (RawShifts[i].LengthSq() > 1e-6f)
                            {
                                Indices.Add(i);
                                Weights.Add(RawShifts[i]);

                                if (Math.Abs(RawShifts[i].X) > 1.5f)
                                    throw new Exception();
                            }
                        }

                        AllWiggleWeights[iwiggle] = (Indices.ToArray(), Weights.ToArray());
                    }, null);
                }

                #endregion

                double[] OldInput = new double[0];
                double[] OldGradient = new double[0];

                #region Loss and gradient functions for warping

                Func<double[], double> WarpEval = input =>
                {
                    SetWarpFromVector(input, this, true);

                    float[] RawCC = GetRawCC(new float2(0));
                    double SumAB = 0, SumA2 = 0, SumB2 = 0;
                    for (int p = 0; p < NParticlesOverall; p++)
                    {
                        for (int t = 0; t < NTilts; t++)
                        {
                            SumAB += RawCC[(p * NTilts + t) * 3 + 0];
                            SumA2 += RawCC[(p * NTilts + t) * 3 + 1];
                            SumB2 += RawCC[(p * NTilts + t) * 3 + 2];
                        }
                    }

                    double Score = SumAB / Math.Max(1e-10, Math.Sqrt(SumA2 * SumB2)) * NParticlesOverall * NTilts * 100;

                    //double[] TiltScores = GetPerTiltDiff2();
                    //double Score = TiltScores.Sum();

                    Console.WriteLine(Score);

                    return Score;
                };

                Func<double[], double[]> WarpGrad = input =>
                {
                    double Delta = 0.025;
                    double Delta2 = Delta * 2;

                    double[] Result = new double[input.Length];

                    if (BFGSIterations-- <= 0)
                        return Result;

                    if (MathHelper.AllEqual(input, OldInput))
                        return OldGradient;

                    int Offset = 0;

                    if ((CurrentOptimizationTypeWarp & WarpOptimizationTypes.ImageWarp) != 0 || // GridMovementXY
                        (CurrentOptimizationTypeWarp & WarpOptimizationTypes.VolumeWarp) != 0)  // GridVolumeWarpXYZ
                    {
                        SetWarpFromVector(input, this, true);
                        (var XP, var XM, var YP, var YM, var Delta2Movement) = GetRawShiftGradients();

                        int NImageWarp = GridMovementX.Values.Length * 2;

                        Parallel.For(0, AllWiggleWeights.Length, iwiggle =>
                        {
                            if (iwiggle < NImageWarp && (CurrentOptimizationTypeWarp & WarpOptimizationTypes.ImageWarp) == 0)
                                return;
                            if (iwiggle >= NImageWarp && (CurrentOptimizationTypeWarp & WarpOptimizationTypes.VolumeWarp) == 0)
                                return;

                            double SumGrad = 0;
                            double SumWeights = 0;
                            double SumWeightsGrad = 0;

                            int[] Indices = AllWiggleWeights[iwiggle].indices;
                            float2[] Weights = AllWiggleWeights[iwiggle].weights;

                            for (int i = 0; i < Indices.Length; i++)
                            {
                                int id = Indices[i];

                                SumWeights += Math.Abs(Weights[i].X) * Math.Sqrt(XP[id * 3 + 1] + XM[id * 3 + 1]) +
                                              Math.Abs(Weights[i].Y) * Math.Sqrt(YP[id * 3 + 1] + YM[id * 3 + 1]);
                                SumWeightsGrad += Math.Abs(Weights[i].X) + Math.Abs(Weights[i].Y);

                                double GradX = (XP[id * 3 + 0] / Math.Max(1e-15, Math.Sqrt(XP[id * 3 + 1] * XP[id * 3 + 2])) -
                                                XM[id * 3 + 0] / Math.Max(1e-15, Math.Sqrt(XM[id * 3 + 1] * XM[id * 3 + 2]))) / Delta2Movement;
                                double GradY = (YP[id * 3 + 0] / Math.Max(1e-15, Math.Sqrt(YP[id * 3 + 1] * YP[id * 3 + 2])) -
                                                YM[id * 3 + 0] / Math.Max(1e-15, Math.Sqrt(YM[id * 3 + 1] * YM[id * 3 + 2]))) / Delta2Movement;

                                SumGrad += Weights[i].X * Math.Sqrt(XP[id * 3 + 1] + XM[id * 3 + 1]) * GradX;
                                SumGrad += Weights[i].Y * Math.Sqrt(YP[id * 3 + 1] + YM[id * 3 + 1]) * GradY;
                            }

                            Result[Offset + iwiggle] = SumGrad / Math.Max(1e-15, SumWeights) * 100 * SumWeightsGrad;
                        });
                    }
                    Offset += AllWiggleWeights.Length;


                    if ((CurrentOptimizationTypeWarp & WarpOptimizationTypes.AxisAngle) != 0) // GridAngleX, Y and Z
                    {
                        int SliceElements = (int)GridAngleX.Dimensions.ElementsSlice();

                        for (int a = 0; a < 3; a++)
                        {
                            for (int i = 0; i < SliceElements; i++)
                            {
                                double[] InputPlus = input.ToArray();
                                for (int t = 0; t < NTilts; t++)
                                    InputPlus[Offset + t * SliceElements + i] += Delta;

                                SetWarpFromVector(InputPlus, this, true);
                                double[] ScoresPlus = GetPerTiltCC();

                                double[] InputMinus = input.ToArray();
                                for (int t = 0; t < NTilts; t++)
                                    InputMinus[Offset + t * SliceElements + i] -= Delta;

                                SetWarpFromVector(InputMinus, this, true);
                                double[] ScoresMinus = GetPerTiltCC();

                                for (int t = 0; t < NTilts; t++)
                                    Result[Offset + t * SliceElements + i] = (ScoresPlus[t] - ScoresMinus[t]) / Delta2;
                            }

                            Offset += GridAngleX.Values.Length;
                        }
                    }
                    else
                    {
                        Offset += GridAngleX.Values.Length * 3;
                    }


                    {
                        for (int ispecies = 0; ispecies < allSpecies.Length; ispecies++)
                        {
                            Species Species = allSpecies[ispecies];
                            Particle[] Particles = SpeciesParticles[Species];

                            int TemporalRes = allSpecies[ispecies].TemporalResolutionMovement;

                            if ((CurrentOptimizationTypeWarp & WarpOptimizationTypes.ParticlePosition) != 0)
                                for (int iparam = 0; iparam < 3 * TemporalRes; iparam++)
                                {
                                    double[] InputPlus = input.ToArray();
                                    for (int p = 0; p < Particles.Length; p++)
                                        InputPlus[Offset + p * 6 * TemporalRes + iparam] += Delta;

                                    SetWarpFromVector(InputPlus, this, true);
                                    double[] ScoresPlus = GetPerParticleCCSpecies(Species);

                                    double[] InputMinus = input.ToArray();
                                    for (int p = 0; p < Particles.Length; p++)
                                        InputMinus[Offset + p * 6 * TemporalRes + iparam] -= Delta;

                                    SetWarpFromVector(InputMinus, this, true);
                                    double[] ScoresMinus = GetPerParticleCCSpecies(Species);

                                    for (int p = 0; p < Particles.Length; p++)
                                        Result[Offset + p * 6 * TemporalRes + iparam] = (ScoresPlus[p] - ScoresMinus[p]) / Delta2;
                                }

                            if ((CurrentOptimizationTypeWarp & WarpOptimizationTypes.ParticleAngle) != 0)
                                for (int iparam = 3 * TemporalRes; iparam < 6 * TemporalRes; iparam++)
                                {
                                    double[] InputPlus = input.ToArray();
                                    for (int p = 0; p < Particles.Length; p++)
                                        InputPlus[Offset + p * 6 * TemporalRes + iparam] += Delta;

                                    SetWarpFromVector(InputPlus, this, true);
                                    double[] ScoresPlus = GetPerParticleCCSpecies(Species);

                                    double[] InputMinus = input.ToArray();
                                    for (int p = 0; p < Particles.Length; p++)
                                        InputMinus[Offset + p * 6 * TemporalRes + iparam] -= Delta;

                                    SetWarpFromVector(InputMinus, this, true);
                                    double[] ScoresMinus = GetPerParticleCCSpecies(Species);

                                    for (int p = 0; p < Particles.Length; p++)
                                        Result[Offset + p * 6 * TemporalRes + iparam] = (ScoresPlus[p] - ScoresMinus[p]) / Delta2;
                                }

                            Offset += OriginalParticlePositions[ispecies].Length * 6; // No * TemporalRes because it's already included in OriginalParticlePositions
                        }
                    }

                    if ((CurrentOptimizationTypeWarp & WarpOptimizationTypes.Zernike13) != 0)
                    {
                        for (int iparam = 0; iparam < Math.Min(6, CTF.ZernikeCoeffsOdd.Length); iparam++)
                        {
                            double[] InputPlus = input.ToArray();
                            InputPlus[Offset + iparam] += Delta;

                            //SetWarpFromVector(InputPlus, this, true);
                            double ScoresPlus = WarpEval(InputPlus);

                            double[] InputMinus = input.ToArray();
                            InputMinus[Offset + iparam] -= Delta;

                            //SetWarpFromVector(InputMinus, this, true);
                            double ScoresMinus = WarpEval(InputMinus);

                            Result[Offset + iparam] = (ScoresPlus - ScoresMinus) / Delta2;
                        }
                    }

                    if ((CurrentOptimizationTypeWarp & WarpOptimizationTypes.Zernike5) != 0)
                    {
                        for (int iparam = Math.Min(6, CTF.ZernikeCoeffsOdd.Length); iparam < Math.Min(12, CTF.ZernikeCoeffsOdd.Length); iparam++)
                        {
                            double[] InputPlus = input.ToArray();
                            InputPlus[Offset + iparam] += Delta;

                            //SetWarpFromVector(InputPlus, this, true);
                            double ScoresPlus = WarpEval(InputPlus);

                            double[] InputMinus = input.ToArray();
                            InputMinus[Offset + iparam] -= Delta;

                            //SetWarpFromVector(InputMinus, this, true);
                            double ScoresMinus = WarpEval(InputMinus);

                            Result[Offset + iparam] = (ScoresPlus - ScoresMinus) / Delta2;
                        }
                    }

                    Offset += CTF.ZernikeCoeffsOdd.Length;

                    if ((CurrentOptimizationTypeWarp & WarpOptimizationTypes.Magnification) != 0)
                    {
                        for (int iparam = 0; iparam < 3; iparam++)
                        {
                            double[] InputPlus = input.ToArray();
                            InputPlus[input.Length - 3 + iparam] += Delta;

                            //SetWarpFromVector(InputPlus, this, true);
                            double ScoresPlus = WarpEval(InputPlus);

                            double[] InputMinus = input.ToArray();
                            InputMinus[input.Length - 3 + iparam] -= Delta;

                            //SetWarpFromVector(InputMinus, this, true);
                            double ScoresMinus = WarpEval(InputMinus);

                            Result[input.Length - 3 + iparam] = (ScoresPlus - ScoresMinus) / Delta2;
                        }
                    }

                    OldInput = input.ToList().ToArray();
                    OldGradient = Result.ToList().ToArray();

                    return Result;
                };

                #endregion

                #region Loss and gradient functions for defocus

                Func<double[], double> DefocusEval = input =>
                {
                    SetDefocusFromVector(input);

                    double ScoreAB = 0, ScoreA2 = 0, ScoreB2 = 0;

                    float AngPixExtract = (float)OptionsDataLoad.BinnedPixelSizeMean;
                    int BatchSize = optionsMPA.BatchSize;
                    float[] ResultP = new float[BatchSize * 3];
                    float[] ResultQ = new float[BatchSize * 3];

                    foreach (var species in allSpecies)
                    {
                        if (!SpeciesParticles.ContainsKey(species))
                            continue;

                        Particle[] Particles = SpeciesParticles[species];
                        int NParticles = Particles.Length;
                        if (NParticles == 0)
                            continue;

                        int SizeRefine = species.HalfMap1Projector[GPUID].Dims.X;
                        int SizeRefineSuper = SizeRefine * SpeciesCTFSuperresFactor[species];
                        int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                        int SizeFullSuper = SizeFull * SpeciesCTFSuperresFactor[species];

                        float AngPixRefine = species.ResolutionRefinement / 2;
                        int ParticleDiameterPix = (int)(species.DiameterAngstrom / AngPixRefine);

                        int[] RelevantSizes = SpeciesRelevantRefinementSizes[species];

                        Image CoordsCTF = CTF.GetCTFCoords(SizeRefineSuper, SizeRefineSuper);   // Not SizeFullSuper because CTF creation later adjusts pixel size to AngPixRefine

                        Image Extracted = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                        Image ExtractedFT = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                        Image ExtractedCropped = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                        Image ExtractedCroppedFT = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                        Image ExtractedCTF = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true, true);

                        int PlanForwSuper = GPU.CreateFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                        int PlanBackSuper = GPU.CreateIFFTPlan(new int3(SizeRefineSuper, SizeRefineSuper, 1), (uint)BatchSize);
                        int PlanForw = GPU.CreateFFTPlan(new int3(SizeRefine, SizeRefine, 1), (uint)BatchSize);

                        if (PlanForwSuper <= 0 || PlanBackSuper <= 0 || PlanForw <= 0)
                        {
                            Console.WriteLine($"SizeFullSuper = {SizeFullSuper}, BatchSize = {BatchSize}, free memory = {GPU.GetFreeMemory(GPUID)}");
                            throw new Exception("No FFT plans created!");
                        }

                        Image PhaseCorrection = CTF.GetPhaseCorrection(AngPixRefine, SizeRefine);
                        Image PhaseCorrectionAll = new Image(new int3(SizeRefine, SizeRefine, NTilts), true, true);
                        for (int t = 0; t < NTilts; t++)
                            GPU.CropFT(PhaseCorrection.GetDevice(Intent.Read),
                                       PhaseCorrectionAll.GetDeviceSlice(t, Intent.Write),
                                       PhaseCorrection.Dims.Slice(),
                                       new int3(RelevantSizes[t]).Slice(),
                                       1);

                        Image GammaCorrection = CTF.GetGammaCorrection(AngPixRefine, SizeRefineSuper);

                        bool[] EwaldReverse = { species.EwaldReverse, !species.EwaldReverse };
                        float[][] EwaldResults = { ResultP, ResultQ };

                        for (int batchStart = 0; batchStart < NParticles; batchStart += BatchSize)
                        {
                            int CurBatch = Math.Min(BatchSize, NParticles - batchStart);
                            IEnumerable<Particle> BatchParticles = Particles.Skip(batchStart).Take(CurBatch);
                            float3[] CoordinatesMoving = Helper.Combine(BatchParticles.Select(p => p.GetCoordinateSeries(DoseInterpolationSteps)));
                            float3[] AnglesMoving = Helper.Combine(BatchParticles.Select(p => p.GetAngleSeries(DoseInterpolationSteps)));

                            for (int t = 0; t < NTilts; t++)
                            {
                                float3[] CoordinatesTilt = new float3[CurBatch];
                                float3[] AnglesTilt = new float3[CurBatch];
                                for (int p = 0; p < CurBatch; p++)
                                {
                                    CoordinatesTilt[p] = CoordinatesMoving[p * NTilts + t];
                                    AnglesTilt[p] = AnglesMoving[p * NTilts + t];
                                }

                                float3[] ImageCoords = GetPositionsInOneTilt(CoordinatesTilt, t);
                                float3[] ImageAngles = GetAnglesInOneTilt(CoordinatesTilt, AnglesTilt, t);

                                float[] Defoci = new float[CurBatch];
                                int3[] ExtractOrigins = new int3[CurBatch];
                                float3[] ResidualShifts = new float3[BatchSize];
                                for (int p = 0; p < CurBatch; p++)
                                {
                                    float ScaledX = ImageCoords[p].X / AngPixExtract, ScaledY = ImageCoords[p].Y / AngPixExtract;
                                    ExtractOrigins[p] = new int3((int)ScaledX - SizeFullSuper / 2, (int)ScaledY - SizeFullSuper / 2, 0);
                                    ResidualShifts[p] = -new float3(ScaledX - (int)ScaledX - SizeFullSuper / 2, ScaledY - (int)ScaledY - SizeFullSuper / 2, 0);
                                    Defoci[p] = ImageCoords[p].Z;
                                }

                                for (int iewald = 0; iewald < (species.DoEwald ? 2 : 1); iewald++)
                                {
                                    GPU.Extract(TiltData[t].GetDevice(Intent.Read),
                                            Extracted.GetDevice(Intent.Write),
                                            TiltData[t].Dims.Slice(),
                                            new int3(SizeFullSuper, SizeFullSuper, 1),
                                            Helper.ToInterleaved(ExtractOrigins),
                                            (uint)CurBatch);

                                    GPU.FFT(Extracted.GetDevice(Intent.Read),
                                            ExtractedFT.GetDevice(Intent.Write),
                                            new int3(SizeFullSuper, SizeFullSuper, 1),
                                            (uint)CurBatch,
                                            PlanForwSuper);

                                    ExtractedFT.ShiftSlices(ResidualShifts);
                                    ExtractedFT.Multiply(1f / (SizeFullSuper * SizeFullSuper));

                                    GPU.CropFT(ExtractedFT.GetDevice(Intent.Read),
                                               Extracted.GetDevice(Intent.Write),
                                               new int3(SizeFullSuper, SizeFullSuper, 1),
                                               new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                               (uint)CurBatch);

                                    if (species.DoEwald)
                                    {
                                        GetComplexCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, EwaldReverse[iewald], ExtractedCTF, true);

                                        GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                           ExtractedCTF.GetDevice(Intent.Read),
                                                                           ExtractedFT.GetDevice(Intent.Write),
                                                                           ExtractedCTF.ElementsComplex,
                                                                           1);
                                    }
                                    else
                                    {
                                        GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, ExtractedCTF, true);

                                        GPU.MultiplyComplexSlicesByScalar(Extracted.GetDevice(Intent.Read),
                                                                          ExtractedCTF.GetDevice(Intent.Read),
                                                                          ExtractedFT.GetDevice(Intent.Write),
                                                                          ExtractedCTF.ElementsComplex,
                                                                          1);
                                    }

                                    GPU.IFFT(ExtractedFT.GetDevice(Intent.Read),
                                             Extracted.GetDevice(Intent.Write),
                                             new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                             (uint)CurBatch,
                                             PlanBackSuper,
                                             false);

                                    GPU.CropFTFull(Extracted.GetDevice(Intent.Read),
                                                    ExtractedCropped.GetDevice(Intent.Write),
                                                    new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                    new int3(SizeRefine, SizeRefine, 1),
                                                    (uint)CurBatch);

                                    GPU.SphereMask(ExtractedCropped.GetDevice(Intent.Read),
                                                   ExtractedCropped.GetDevice(Intent.Write),
                                                   ExtractedCropped.Dims.Slice(),
                                                   ParticleDiameterPix / 2f,
                                                   16 * AngPixExtract / AngPixRefine,
                                                   true,
                                                   (uint)CurBatch);

                                    GPU.FFT(ExtractedCropped.GetDevice(Intent.Read),
                                            ExtractedCroppedFT.GetDevice(Intent.Write),
                                            new int3(SizeRefine, SizeRefine, 1),
                                            (uint)CurBatch,
                                            PlanForw);

                                    ExtractedCroppedFT.Multiply(1f / (SizeRefine * SizeRefine));

                                    GPU.CropFT(ExtractedCroppedFT.GetDevice(Intent.Read),
                                               ExtractedCropped.GetDevice(Intent.Write),
                                               new int3(SizeRefine).Slice(),
                                               new int3(RelevantSizes[t]).Slice(),
                                               (uint)CurBatch);


                                    GPU.MultiParticleDiff(EwaldResults[iewald],
                                                          new IntPtr[] { ExtractedCropped.GetDevice(Intent.Read) },
                                                          SizeRefine,
                                                          new[] { RelevantSizes[t] },
                                                          new float[CurBatch * 2],
                                                          Helper.ToInterleaved(ImageAngles),
                                                          MagnificationCorrection,
                                                          SpeciesCTFWeights[species].GetDeviceSlice(t, Intent.Read),
                                                          PhaseCorrectionAll.GetDeviceSlice(t, Intent.Read),
                                                          species.DoEwald ? CTF.GetEwaldRadius(SizeFull, (float)species.PixelSize) * (iewald == 0 ? 1 : -1) : 0,
                                                          species.CurrentMaxShellRefinement,
                                                          new[] { species.HalfMap1Projector[GPUID].t_DataRe, species.HalfMap2Projector[GPUID].t_DataRe },
                                                          new[] { species.HalfMap1Projector[GPUID].t_DataIm, species.HalfMap2Projector[GPUID].t_DataIm },
                                                          species.HalfMap1Projector[GPUID].Oversampling,
                                                          species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                                          new IntPtr((long)SpeciesParticleSubsets[species] + batchStart * sizeof(int)),
                                                          CurBatch,
                                                          1);
                                }

                                for (int i = 0; i < CurBatch; i++)
                                {
                                    ScoreAB += ResultP[i * 3 + 0] + ResultQ[i * 3 + 0];
                                    ScoreA2 += ResultP[i * 3 + 1] + ResultQ[i * 3 + 1];
                                    ScoreB2 += ResultP[i * 3 + 2] + ResultQ[i * 3 + 2];
                                }
                            }
                        }

                        PhaseCorrectionAll.Dispose();
                        PhaseCorrection.Dispose();
                        GammaCorrection.Dispose();

                        CoordsCTF.Dispose();
                        Extracted.Dispose();
                        ExtractedFT.Dispose();
                        ExtractedCropped.Dispose();
                        ExtractedCroppedFT.Dispose();
                        ExtractedCTF.Dispose();

                        GPU.DestroyFFTPlan(PlanForwSuper);
                        GPU.DestroyFFTPlan(PlanBackSuper);
                        GPU.DestroyFFTPlan(PlanForw);
                    }

                    //foreach (var image in TiltData)
                    //    image.FreeDevice();

                    double Score = ScoreAB / Math.Max(1e-10, Math.Sqrt(ScoreA2 * ScoreB2)) * NParticlesOverall * NTilts;
                    Score *= 100;

                    Console.WriteLine(Score);

                    return Score;
                };

                Func<double[], double[]> DefocusGrad = input =>
                {
                    double Delta = 0.001;
                    double Delta2 = Delta * 2;

                    double[] Deltas = { Delta, -Delta };

                    double[] Result = new double[input.Length];
                    double[] ScoresAB = new double[input.Length * 2];
                    double[] ScoresA2 = new double[input.Length * 2];
                    double[] ScoresB2 = new double[input.Length * 2];
                    int[] ScoresSamples = new int[input.Length * 2];

                    if (BFGSIterations-- <= 0)
                        return Result;

                    if (MathHelper.AllEqual(input, OldInput))
                        return OldGradient;

                    float AngPixExtract = (float)OptionsDataLoad.BinnedPixelSizeMean;
                    int BatchSize = 64;
                    float[] ResultP = new float[BatchSize * 3];
                    float[] ResultQ = new float[BatchSize * 3];

                    foreach (var species in allSpecies)
                    {
                        if (!SpeciesParticles.ContainsKey(species))
                            continue;

                        Particle[] Particles = SpeciesParticles[species];
                        int NParticles = Particles.Length;
                        int SizeRefine = species.HalfMap1Projector[GPUID].Dims.X;
                        int SizeRefineSuper = SizeRefine * SpeciesCTFSuperresFactor[species];
                        int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                        int SizeFullSuper = SizeFull * SpeciesCTFSuperresFactor[species];
                        float AngPixRefine = species.ResolutionRefinement / 2;
                        int ParticleDiameterPix = (int)(species.DiameterAngstrom / AngPixRefine);

                        int[] RelevantSizes = SpeciesRelevantRefinementSizes[species];

                        Image CoordsCTF = CTF.GetCTFCoords(SizeRefineSuper, SizeRefineSuper);

                        Image Extracted = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                        Image ExtractedFT = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                        Image ExtractedRefineSuper = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true, true);
                        Image ExtractedCropped = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                        Image ExtractedCroppedFT = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                        Image ExtractedCTF = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true, true);

                        int PlanForwSuper = GPU.CreateFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                        int PlanBackSuper = GPU.CreateIFFTPlan(new int3(SizeRefineSuper, SizeRefineSuper, 1), (uint)BatchSize);
                        int PlanForw = GPU.CreateFFTPlan(new int3(SizeRefine, SizeRefine, 1), (uint)BatchSize);

                        if (PlanForwSuper <= 0 || PlanBackSuper <= 0 || PlanForw <= 0)
                        {
                            Console.WriteLine($"SizeFullSuper = {SizeFullSuper}, BatchSize = {BatchSize}, free memory = {GPU.GetFreeMemory(GPUID)}");
                            throw new Exception("No FFT plans created!");
                        }

                        Image PhaseCorrection = CTF.GetPhaseCorrection(AngPixRefine, SizeRefine);
                        Image PhaseCorrectionAll = new Image(new int3(SizeRefine, SizeRefine, NTilts), true, true);
                        for (int t = 0; t < NTilts; t++)
                            GPU.CropFT(PhaseCorrection.GetDevice(Intent.Read),
                                        PhaseCorrectionAll.GetDeviceSlice(t, Intent.Write),
                                        PhaseCorrection.Dims.Slice(),
                                        new int3(RelevantSizes[t]).Slice(),
                                        1);

                        bool[] EwaldReverse = { species.EwaldReverse, !species.EwaldReverse };
                        float[][] EwaldResults = { ResultP, ResultQ };

                        for (int batchStart = 0; batchStart < NParticles; batchStart += BatchSize)
                        {
                            int CurBatch = Math.Min(BatchSize, NParticles - batchStart);
                            IEnumerable<Particle> BatchParticles = Particles.Skip(batchStart).Take(CurBatch);
                            float3[] CoordinatesMoving = Helper.Combine(BatchParticles.Select(p => p.GetCoordinateSeries(DoseInterpolationSteps)));
                            float3[] AnglesMoving = Helper.Combine(BatchParticles.Select(p => p.GetAngleSeries(DoseInterpolationSteps)));

                            for (int t = 0; t < NTilts; t++)
                            {
                                float3[] CoordinatesTilt = new float3[CurBatch];
                                float3[] AnglesTilt = new float3[CurBatch];
                                for (int p = 0; p < CurBatch; p++)
                                {
                                    CoordinatesTilt[p] = CoordinatesMoving[p * NTilts + t];
                                    AnglesTilt[p] = AnglesMoving[p * NTilts + t];
                                }

                                float3[] ImageCoords = GetPositionsInOneTilt(CoordinatesTilt, t);
                                float3[] ImageAngles = GetAnglesInOneTilt(CoordinatesTilt, AnglesTilt, t);

                                float[] Defoci = new float[CurBatch];
                                int3[] ExtractOrigins = new int3[CurBatch];
                                float3[] ResidualShifts = new float3[BatchSize];
                                for (int p = 0; p < CurBatch; p++)
                                {
                                    float ScaledX = ImageCoords[p].X / AngPixExtract, ScaledY = ImageCoords[p].Y / AngPixExtract;
                                    ExtractOrigins[p] = new int3((int)ScaledX - SizeFullSuper / 2, (int)ScaledY - SizeFullSuper / 2, 0);
                                    ResidualShifts[p] = -new float3(ScaledX - (int)ScaledX - SizeFullSuper / 2, ScaledY - (int)ScaledY - SizeFullSuper / 2, 0);
                                    Defoci[p] = ImageCoords[p].Z;
                                }

                                GPU.Extract(TiltData[t].GetDevice(Intent.Read),
                                            Extracted.GetDevice(Intent.Write),
                                            TiltData[t].Dims.Slice(),
                                            new int3(SizeFullSuper, SizeFullSuper, 1),
                                            Helper.ToInterleaved(ExtractOrigins),
                                            (uint)CurBatch);

                                GPU.FFT(Extracted.GetDevice(Intent.Read),
                                        ExtractedFT.GetDevice(Intent.Write),
                                        new int3(SizeFullSuper, SizeFullSuper, 1),
                                        (uint)CurBatch,
                                        PlanForwSuper);

                                ExtractedFT.ShiftSlices(ResidualShifts);
                                ExtractedFT.Multiply(1f / (SizeFullSuper * SizeFullSuper));

                                GPU.CropFT(ExtractedFT.GetDevice(Intent.Read),
                                           Extracted.GetDevice(Intent.Write),
                                           new int3(SizeFullSuper, SizeFullSuper, 1),
                                           new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                           (uint)CurBatch);

                                for (int iparam = 0; iparam < CTFStepTypes.Length; iparam++)
                                {
                                    if ((CurrentOptimizationTypeCTF & CTFStepTypes[iparam]) == 0)
                                        continue;

                                    for (int idelta = 0; idelta < 2; idelta++)
                                    {
                                        double[] InputAltered = input.ToArray();
                                        if (iparam < 4)
                                            InputAltered[t * 4 + iparam] += Deltas[idelta];
                                        else
                                            InputAltered[input.Length - CTFStepTypes.Length + iparam] += Deltas[idelta];

                                        SetDefocusFromVector(InputAltered);

                                        ImageCoords = GetPositionsInOneTilt(CoordinatesTilt, t);
                                        for (int p = 0; p < CurBatch; p++)
                                            Defoci[p] = ImageCoords[p].Z;


                                        Image GammaCorrection = CTF.GetGammaCorrection(AngPixRefine, SizeRefineSuper);

                                        for (int iewald = 0; iewald < (species.DoEwald ? 2 : 1); iewald++)
                                        {
                                            if (species.DoEwald)
                                            {
                                                GetComplexCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, EwaldReverse[iewald], ExtractedCTF, true);

                                                GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                                  ExtractedCTF.GetDevice(Intent.Read),
                                                                                  ExtractedFT.GetDevice(Intent.Write),
                                                                                  ExtractedCTF.ElementsComplex,
                                                                                  1);
                                            }
                                            else
                                            {
                                                GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, ExtractedCTF, true);

                                                GPU.MultiplyComplexSlicesByScalar(Extracted.GetDevice(Intent.Read),
                                                                                  ExtractedCTF.GetDevice(Intent.Read),
                                                                                  ExtractedFT.GetDevice(Intent.Write),
                                                                                  ExtractedCTF.ElementsComplex,
                                                                                  1);
                                            }

                                            GPU.IFFT(ExtractedFT.GetDevice(Intent.Read),
                                                     ExtractedRefineSuper.GetDevice(Intent.Write),
                                                     new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                     (uint)CurBatch,
                                                     PlanBackSuper,
                                                     false);

                                            GPU.CropFTFull(ExtractedRefineSuper.GetDevice(Intent.Read),
                                                            ExtractedCropped.GetDevice(Intent.Write),
                                                            new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                            new int3(SizeRefine, SizeRefine, 1),
                                                            (uint)CurBatch);

                                            GPU.SphereMask(ExtractedCropped.GetDevice(Intent.Read),
                                                           ExtractedCropped.GetDevice(Intent.Write),
                                                           ExtractedCropped.Dims.Slice(),
                                                           ParticleDiameterPix / 2f,
                                                           16 * AngPixExtract / AngPixRefine,
                                                           true,
                                                           (uint)CurBatch);

                                            GPU.FFT(ExtractedCropped.GetDevice(Intent.Read),
                                                    ExtractedCroppedFT.GetDevice(Intent.Write),
                                                    new int3(SizeRefine, SizeRefine, 1),
                                                    (uint)CurBatch,
                                                    PlanForw);

                                            ExtractedCroppedFT.Multiply(1f / (SizeRefine * SizeRefine));

                                            GPU.CropFT(ExtractedCroppedFT.GetDevice(Intent.Read),
                                                       ExtractedCropped.GetDevice(Intent.Write),
                                                       new int3(SizeRefine).Slice(),
                                                       new int3(RelevantSizes[t]).Slice(),
                                                       (uint)CurBatch);


                                            GPU.MultiParticleDiff(EwaldResults[iewald],
                                                                  new IntPtr[] { ExtractedCropped.GetDevice(Intent.Read) },
                                                                  SizeRefine,
                                                                  new[] { RelevantSizes[t] },
                                                                  new float[CurBatch * 2],
                                                                  Helper.ToInterleaved(ImageAngles),
                                                                  MagnificationCorrection,
                                                                  SpeciesCTFWeights[species].GetDeviceSlice(t, Intent.Read),
                                                                  PhaseCorrectionAll.GetDeviceSlice(t, Intent.Read),
                                                                  species.DoEwald ? CTF.GetEwaldRadius(SizeFull, (float)species.PixelSize) * (iewald == 0 ? 1 : -1) : 0,
                                                                  species.CurrentMaxShellRefinement,
                                                                  new[] { species.HalfMap1Projector[GPUID].t_DataRe, species.HalfMap2Projector[GPUID].t_DataRe },
                                                                  new[] { species.HalfMap1Projector[GPUID].t_DataIm, species.HalfMap2Projector[GPUID].t_DataIm },
                                                                  species.HalfMap1Projector[GPUID].Oversampling,
                                                                  species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                                                  new IntPtr((long)SpeciesParticleSubsets[species] + batchStart * sizeof(int)),
                                                                  CurBatch,
                                                                  1);
                                        }

                                        GammaCorrection.Dispose();

                                        if (iparam < 4)
                                            for (int i = 0; i < CurBatch; i++)
                                            {
                                                ScoresAB[(t * 4 + iparam) * 2 + idelta] += ResultP[i * 3 + 0] + ResultQ[i * 3 + 0];
                                                ScoresA2[(t * 4 + iparam) * 2 + idelta] += ResultP[i * 3 + 1] + ResultQ[i * 3 + 1];
                                                ScoresB2[(t * 4 + iparam) * 2 + idelta] += ResultP[i * 3 + 2] + ResultQ[i * 3 + 2];
                                                ScoresSamples[(t * 4 + iparam) * 2 + idelta]++;
                                            }
                                        else
                                            for (int i = 0; i < CurBatch; i++)
                                            {
                                                ScoresAB[(input.Length - CTFStepTypes.Length + iparam) * 2 + idelta] += ResultP[i * 3 + 0] + ResultQ[i * 3 + 0];
                                                ScoresA2[(input.Length - CTFStepTypes.Length + iparam) * 2 + idelta] += ResultP[i * 3 + 1] + ResultQ[i * 3 + 1];
                                                ScoresB2[(input.Length - CTFStepTypes.Length + iparam) * 2 + idelta] += ResultP[i * 3 + 2] + ResultQ[i * 3 + 2];
                                                ScoresSamples[(input.Length - CTFStepTypes.Length + iparam) * 2 + idelta]++;
                                            }
                                    }
                                }
                            }
                        }

                        PhaseCorrectionAll.Dispose();
                        PhaseCorrection.Dispose();

                        CoordsCTF.Dispose();
                        Extracted.Dispose();
                        ExtractedFT.Dispose();
                        ExtractedRefineSuper.Dispose();
                        ExtractedCropped.Dispose();
                        ExtractedCroppedFT.Dispose();
                        ExtractedCTF.Dispose();

                        GPU.DestroyFFTPlan(PlanForwSuper);
                        GPU.DestroyFFTPlan(PlanBackSuper);
                        GPU.DestroyFFTPlan(PlanForw);
                    }

                    //foreach (var image in TiltData)
                    //    image.FreeDevice();

                    for (int i = 0; i < ScoresAB.Length; i++)
                        ScoresAB[i] = ScoresAB[i] / Math.Max(1e-10, Math.Sqrt(ScoresA2[i] * ScoresB2[i])) * ScoresSamples[i];

                    for (int i = 0; i < Result.Length; i++)
                        Result[i] = (ScoresAB[i * 2 + 0] - ScoresAB[i * 2 + 1]) / Delta2 * 100;

                    OldInput = input.ToList().ToArray();
                    OldGradient = Result.ToList().ToArray();

                    return Result;
                };

                #endregion

                #region Grid search for per-tilt defoci

                Func<double[], double[]> DefocusGridSearch = input =>
                {
                    float AngPixExtract = (float)OptionsDataLoad.BinnedPixelSizeMean;
                    int BatchSize = optionsMPA.BatchSize;
                    float[] ResultP = new float[BatchSize * 3];
                    float[] ResultQ = new float[BatchSize * 3];

                    List<float4>[] AllSearchValues = Helper.ArrayOfFunction(i => new List<float4>(), NTilts);
                    List<float4>[] CurrentSearchValues = Helper.ArrayOfFunction(i => new List<float4>(), NTilts);
                    decimal GridSearchDelta = 0.05M;
                    foreach (var list in CurrentSearchValues)
                    {
                        for (decimal d = -3M; d <= 3M; d += GridSearchDelta)
                            list.Add(new float4((float)d, 0, 0, 0));
                    }

                    for (int irefine = 0; irefine < 4; irefine++)
                    {
                        foreach (var species in allSpecies)
                        {
                            if (!SpeciesParticles.ContainsKey(species))
                                continue;
                            Particle[] Particles = SpeciesParticles[species];
                            int NParticles = Particles.Length;
                            if (NParticles == 0)
                                continue;

                            int SpeciesOffset = SpeciesParticleIDRanges[species].Start;

                            int SizeRefine = species.HalfMap1Projector[GPUID].Dims.X;
                            int SizeRefineSuper = SizeRefine * SpeciesCTFSuperresFactor[species];
                            int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                            int SizeFullSuper = SizeFull * SpeciesCTFSuperresFactor[species];
                            float AngPixRefine = species.ResolutionRefinement / 2;
                            int ParticleDiameterPix = (int)(species.DiameterAngstrom / AngPixRefine);

                            int[] RelevantSizes = SpeciesRelevantRefinementSizes[species];

                            Image CoordsCTF = CTF.GetCTFCoords(SizeRefineSuper, SizeRefineSuper);

                            Image Extracted = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                            Image ExtractedFT = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                            Image ExtractedRefineSuper = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true, true);
                            Image ExtractedCropped = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                            Image ExtractedCroppedFT = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                            Image ExtractedCTF = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true, true);

                            Image PhaseCorrection = CTF.GetPhaseCorrection(AngPixRefine, SizeRefine);
                            Image PhaseCorrectionAll = new Image(new int3(SizeRefine, SizeRefine, NTilts), true, true);
                            for (int t = 0; t < NTilts; t++)
                                GPU.CropFT(PhaseCorrection.GetDevice(Intent.Read),
                                            PhaseCorrectionAll.GetDeviceSlice(t, Intent.Write),
                                            PhaseCorrection.Dims.Slice(),
                                            new int3(RelevantSizes[t]).Slice(),
                                            1);

                            Image GammaCorrection = CTF.GetGammaCorrection(AngPixRefine, SizeRefineSuper);

                            bool[] EwaldReverse = { species.EwaldReverse, !species.EwaldReverse };
                            float[][] EwaldResults = { ResultP, ResultQ };

                            int PlanForwSuper = GPU.CreateFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                            int PlanBackSuper = GPU.CreateIFFTPlan(new int3(SizeRefineSuper, SizeRefineSuper, 1), (uint)BatchSize);
                            int PlanForw = GPU.CreateFFTPlan(new int3(SizeRefine, SizeRefine, 1), (uint)BatchSize);

                            if (PlanForwSuper <= 0 || PlanBackSuper <= 0 || PlanForw <= 0)
                            {
                                Console.WriteLine($"SizeFullSuper = {SizeFullSuper}, BatchSize = {BatchSize}, free memory = {GPU.GetFreeMemory(GPUID)}");
                                throw new Exception("No FFT plans created!");
                            }
                            GPU.CheckGPUExceptions();

                            for (int batchStart = 0; batchStart < NParticles; batchStart += BatchSize)
                            {
                                int CurBatch = Math.Min(BatchSize, NParticles - batchStart);
                                IEnumerable<Particle> BatchParticles = Particles.Skip(batchStart).Take(CurBatch);
                                float3[] CoordinatesMoving = Helper.Combine(BatchParticles.Select(p => p.GetCoordinateSeries(DoseInterpolationSteps)));
                                float3[] AnglesMoving = Helper.Combine(BatchParticles.Select(p => p.GetAngleSeries(DoseInterpolationSteps)));

                                for (int t = 0; t < NTilts; t++)
                                {
                                    float3[] CoordinatesTilt = new float3[CurBatch];
                                    float3[] AnglesTilt = new float3[CurBatch];
                                    for (int p = 0; p < CurBatch; p++)
                                    {
                                        CoordinatesTilt[p] = CoordinatesMoving[p * NTilts + t];
                                        AnglesTilt[p] = AnglesMoving[p * NTilts + t];
                                    }

                                    float3[] ImageCoords = GetPositionsInOneTilt(CoordinatesTilt, t);
                                    float3[] ImageAngles = GetAnglesInOneTilt(CoordinatesTilt, AnglesTilt, t);

                                    float[] Defoci = new float[CurBatch];
                                    int3[] ExtractOrigins = new int3[CurBatch];
                                    float3[] ResidualShifts = new float3[BatchSize];
                                    for (int p = 0; p < CurBatch; p++)
                                    {
                                        float ScaledX = ImageCoords[p].X / AngPixExtract, ScaledY = ImageCoords[p].Y / AngPixExtract;
                                        ExtractOrigins[p] = new int3((int)ScaledX - SizeFullSuper / 2, (int)ScaledY - SizeFullSuper / 2, 0);
                                        ResidualShifts[p] = -new float3(ScaledX - (int)ScaledX - SizeFullSuper / 2, ScaledY - (int)ScaledY - SizeFullSuper / 2, 0);
                                        Defoci[p] = ImageCoords[p].Z;
                                    }

                                    GPU.Extract(TiltData[t].GetDevice(Intent.Read),
                                                Extracted.GetDevice(Intent.Write),
                                                TiltData[t].Dims.Slice(),
                                                new int3(SizeFullSuper, SizeFullSuper, 1),
                                                Helper.ToInterleaved(ExtractOrigins),
                                                (uint)CurBatch);

                                    GPU.FFT(Extracted.GetDevice(Intent.Read),
                                            ExtractedFT.GetDevice(Intent.Write),
                                            new int3(SizeFullSuper, SizeFullSuper, 1),
                                            (uint)CurBatch,
                                            PlanForwSuper);

                                    ExtractedFT.ShiftSlices(ResidualShifts);
                                    ExtractedFT.Multiply(1f / (SizeFullSuper * SizeFullSuper));

                                    GPU.CropFT(ExtractedFT.GetDevice(Intent.Read),
                                               Extracted.GetDevice(Intent.Write),
                                               new int3(SizeFullSuper, SizeFullSuper, 1),
                                               new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                               (uint)CurBatch);

                                    for (int idelta = 0; idelta < CurrentSearchValues[t].Count; idelta++)
                                    {
                                        double[] InputAltered = input.ToArray();
                                        InputAltered[t * 4 + 0] += CurrentSearchValues[t][idelta].X;

                                        SetDefocusFromVector(InputAltered);

                                        ImageCoords = GetPositionsInOneTilt(CoordinatesTilt, t);
                                        for (int p = 0; p < CurBatch; p++)
                                            Defoci[p] = ImageCoords[p].Z;

                                        for (int iewald = 0; iewald < (species.DoEwald ? 2 : 1); iewald++)
                                        {
                                            if (species.DoEwald)
                                            {
                                                GetComplexCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, EwaldReverse[iewald], ExtractedCTF, true);

                                                GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                                    ExtractedCTF.GetDevice(Intent.Read),
                                                                                    ExtractedFT.GetDevice(Intent.Write),
                                                                                    ExtractedCTF.ElementsComplex,
                                                                                    1);
                                            }
                                            else
                                            {
                                                GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, ExtractedCTF, true);

                                                GPU.MultiplyComplexSlicesByScalar(Extracted.GetDevice(Intent.Read),
                                                                                    ExtractedCTF.GetDevice(Intent.Read),
                                                                                    ExtractedFT.GetDevice(Intent.Write),
                                                                                    ExtractedCTF.ElementsComplex,
                                                                                    1);
                                            }

                                            GPU.IFFT(ExtractedFT.GetDevice(Intent.Read),
                                                        ExtractedRefineSuper.GetDevice(Intent.Write),
                                                        new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                        (uint)CurBatch,
                                                        PlanBackSuper,
                                                        false);

                                            GPU.CropFTFull(ExtractedRefineSuper.GetDevice(Intent.Read),
                                                            ExtractedCropped.GetDevice(Intent.Write),
                                                            new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                            new int3(SizeRefine, SizeRefine, 1),
                                                            (uint)CurBatch);

                                            GPU.SphereMask(ExtractedCropped.GetDevice(Intent.Read),
                                                            ExtractedCropped.GetDevice(Intent.Write),
                                                            ExtractedCropped.Dims.Slice(),
                                                            ParticleDiameterPix / 2f,
                                                            16 * AngPixExtract / AngPixRefine,
                                                            true,
                                                            (uint)CurBatch);

                                            GPU.FFT(ExtractedCropped.GetDevice(Intent.Read),
                                                    ExtractedCroppedFT.GetDevice(Intent.Write),
                                                    new int3(SizeRefine, SizeRefine, 1),
                                                    (uint)CurBatch,
                                                    PlanForw);

                                            ExtractedCroppedFT.Multiply(1f / (SizeRefine * SizeRefine));

                                            GPU.CropFT(ExtractedCroppedFT.GetDevice(Intent.Read),
                                                       ExtractedCropped.GetDevice(Intent.Write),
                                                       new int3(SizeRefine).Slice(),
                                                       new int3(RelevantSizes[t]).Slice(),
                                                       (uint)CurBatch);


                                            GPU.MultiParticleDiff(EwaldResults[iewald],
                                                                    new IntPtr[] { ExtractedCropped.GetDevice(Intent.Read) },
                                                                    SizeRefine,
                                                                    new[] { RelevantSizes[t] },
                                                                    new float[CurBatch * 2],
                                                                    Helper.ToInterleaved(ImageAngles),
                                                                    MagnificationCorrection,
                                                                    SpeciesCTFWeights[species].GetDeviceSlice(t, Intent.Read),
                                                                    PhaseCorrectionAll.GetDeviceSlice(t, Intent.Read),
                                                                    species.DoEwald ? CTF.GetEwaldRadius(SizeFull, (float)species.PixelSize) * (iewald == 0 ? 1 : -1) : 0,
                                                                    species.CurrentMaxShellRefinement,
                                                                    new[] { species.HalfMap1Projector[GPUID].t_DataRe, species.HalfMap2Projector[GPUID].t_DataRe },
                                                                    new[] { species.HalfMap1Projector[GPUID].t_DataIm, species.HalfMap2Projector[GPUID].t_DataIm },
                                                                    species.HalfMap1Projector[GPUID].Oversampling,
                                                                    species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                                                    new IntPtr((long)SpeciesParticleSubsets[species] + batchStart * sizeof(int)),
                                                                    CurBatch,
                                                                    1);
                                        }

                                        for (int i = 0; i < CurBatch; i++)
                                            CurrentSearchValues[t][idelta] += new float4(0,
                                                                                         ResultP[i * 3 + 0] + ResultQ[i * 3 + 0],
                                                                                         ResultP[i * 3 + 1] + ResultQ[i * 3 + 1],
                                                                                         ResultP[i * 3 + 2] + ResultQ[i * 3 + 2]);
                                    }
                                }
                            }

                            PhaseCorrectionAll.Dispose();
                            PhaseCorrection.Dispose();
                            GammaCorrection.Dispose();
                            CoordsCTF.Dispose();
                            Extracted.Dispose();
                            ExtractedFT.Dispose();
                            ExtractedRefineSuper.Dispose();
                            ExtractedCropped.Dispose();
                            ExtractedCroppedFT.Dispose();
                            ExtractedCTF.Dispose();

                            GPU.DestroyFFTPlan(PlanForwSuper);
                            GPU.DestroyFFTPlan(PlanBackSuper);
                            GPU.DestroyFFTPlan(PlanForw);
                        }

                        GridSearchDelta /= 2;
                        for (int t = 0; t < NTilts; t++)
                        {
                            CurrentSearchValues[t].Sort((a, b) => -((a.Y / Math.Max(1e-20, Math.Sqrt(a.Z * a.W))).CompareTo(b.Y / Math.Max(1e-20, Math.Sqrt(b.Z * b.W)))));
                            AllSearchValues[t].AddRange(CurrentSearchValues[t]);

                            List<float4> NewSearchValues = new List<float4>();
                            for (int j = 0; j < 2; j++)
                            {
                                NewSearchValues.Add(new float4(CurrentSearchValues[t][j].X + (float)GridSearchDelta, 0, 0, 0));
                                NewSearchValues.Add(new float4(CurrentSearchValues[t][j].X - (float)GridSearchDelta, 0, 0, 0));
                            }

                            CurrentSearchValues[t] = NewSearchValues;
                        }
                    }

                    for (int i = 0; i < NTilts; i++)
                    {
                        AllSearchValues[i].Sort((a, b) => -((a.Y / Math.Max(1e-10, Math.Sqrt(a.Z * a.W))).CompareTo(b.Y / Math.Max(1e-10, Math.Sqrt(b.Z * b.W)))));
                        input[i * 4 + 0] += AllSearchValues[i][0].X;
                    }

                    return input;
                };

                #endregion

                BroydenFletcherGoldfarbShanno OptimizerWarp = new BroydenFletcherGoldfarbShanno(InitialParametersWarp.Length, WarpEval, WarpGrad);
                BroydenFletcherGoldfarbShanno OptimizerDefocus = new BroydenFletcherGoldfarbShanno(InitialParametersDefocus.Length, DefocusEval, DefocusGrad);

                //WarpEval(InitialParametersWarp);

                bool NeedReextraction = true;

                for (int ioptim = 0; ioptim < optionsMPA.NIterations; ioptim++)
                {
                    foreach (var species in allSpecies)
                        species.CurrentMaxShellRefinement = (int)Math.Round(MathHelper.Lerp(optionsMPA.InitialResolutionPercent / 100f,
                                                                                            1f,
                                                                                            optionsMPA.NIterations == 1 ? 1 : ((float)ioptim / (optionsMPA.NIterations - 1))) *
                                                                            species.HalfMap1Projector[GPUID].Dims.X / 2);

                    if (NeedReextraction)
                    {
                        progressCallback($"Re-extracting particles for optimization iteration {ioptim + 1}/{optionsMPA.NIterations}");
                        ReextractPaddedParticles(false);
                    }
                    NeedReextraction = false;

                    foreach (var step in OptimizationStepsWarp)
                    {
                        progressCallback($"Running optimization iteration {ioptim + 1}/{optionsMPA.NIterations}, " + step.Name);

                        BFGSIterations = step.Iterations;
                        CurrentOptimizationTypeWarp = step.Type;
                        CurrentWeightsDict = SpeciesCTFWeights;

                        OptimizerWarp.Maximize(InitialParametersWarp);

                        OldInput = null;
                    }

                    if (allSpecies.Any(s => s.ResolutionRefinement < (float)optionsMPA.MinimumCTFRefinementResolution))
                    {
                        //ReextractPaddedParticles();
                        //WarpEval(InitialParametersWarp);

                        if (ioptim == 0 && optionsMPA.DoDefocusGridSearch)
                        {
                            progressCallback($"Running optimization iteration {ioptim + 1}/{optionsMPA.NIterations}, defocus grid search");

                            InitialParametersDefocus = DefocusGridSearch(InitialParametersDefocus);

                            NeedReextraction = true;
                        }

                        //CurrentWeightsDict = SpeciesFrameWeights;
                        //ReextractPaddedParticles();
                        //WarpEval(InitialParametersWarp);

                        foreach (var step in OptimizationStepsCTF)
                        {
                            progressCallback($"Running optimization iteration {ioptim + 1}/{optionsMPA.NIterations}, " + step.Name);

                            BFGSIterations = step.Iterations;
                            CurrentOptimizationTypeCTF = step.Type;
                            CurrentWeightsDict = SpeciesCTFWeights;

                            OptimizerDefocus.Maximize(InitialParametersDefocus);

                            OldInput = null;
                            NeedReextraction = true;
                        }
                    }
                }

                SetWarpFromVector(InitialParametersWarp, this, true);
                SetDefocusFromVector(InitialParametersDefocus);

                Console.WriteLine($"{GPU.GetFreeMemory(GPUID)} MB after optimization of {Name}");

                #region Compute NCC for each particle to be able to take only N % of the best later

                {
                    double[] AllParticleScores = GetPerParticleCC();

                    foreach (var species in allSpecies)
                    {
                        Particle[] Particles = SpeciesParticles[species];

                        int NParticles = Particles.Length;
                        if (NParticles == 0)
                            continue;

                        double[] ParticleScores = Helper.Subset(AllParticleScores, SpeciesParticleIDRanges[species].Start, SpeciesParticleIDRanges[species].End);

                        List<int> IndicesSorted = Helper.ArrayOfSequence(0, NParticles, 1).ToList();
                        IndicesSorted.Sort((a, b) => ParticleScores[a].CompareTo(ParticleScores[b]));
                        int FirstGoodIndex = (int)(NParticles * 0.0);

                        float[] Mask = new float[NParticles];
                        for (int i = 0; i < NParticles; i++)
                            Mask[IndicesSorted[i]] = (i >= FirstGoodIndex ? 1f : 0f);

                        GoodParticleMasks.Add(species, Mask);
                    }
                }

                #endregion

                #region Compute FSC between refs and particles to estimate tilt and series weights

                if (true)
                {
                    progressCallback($"Calculating FRC between projections and particles for weight optimization");

                    int FSCLength = 128;
                    Image FSC = new Image(new int3(FSCLength, FSCLength, NTilts * 3), true);
                    Image FSCPerParticle = new Image(new int3(FSCLength / 2, NParticlesOverall * 3, 1));
                    //float[][] FSCData = FSC.GetHost(Intent.ReadWrite);
                    //float[][] FSCPerParticleData = FSCPerParticle.GetHost(Intent.ReadWrite);
                    Image PhaseResiduals = new Image(new int3(FSCLength, FSCLength, 2), true);

                    Star TableOut = new Star(new string[] { "wrpNormCoordinateX", "wrpNormCoordinateY", "wrpNormCoordinateZ" });

                    int BatchSize = optionsMPA.BatchSize;
                    float AngPixExtract = (float)OptionsDataLoad.BinnedPixelSizeMean;

                    for (int ispecies = 0; ispecies < allSpecies.Length; ispecies++)
                    {
                        Species Species = allSpecies[ispecies];
                        Particle[] Particles = SpeciesParticles[Species];

                        int NParticles = Particles.Length;
                        float SpeciesAngPix = Species.ResolutionRefinement / 2;
                        if (NParticles == 0)
                            continue;

                        int SpeciesOffset = SpeciesParticleIDRanges[Species].Start;

                        int SizeRefine = SpeciesRefinementSize[Species];
                        int[] RelevantSizes = SpeciesRelevantRefinementSizes[Species];

                        //Image CorrAB = new Image(new int3(SizeRefine, SizeRefine, NTilts), true);
                        //Image CorrA2 = new Image(new int3(SizeRefine, SizeRefine, NTilts), true);
                        //Image CorrB2 = new Image(new int3(SizeRefine, SizeRefine, NTilts), true);

                        float ScaleFactor = (float)Species.PixelSize * (FSCLength / 2 - 1) /
                                            (float)(Species.ResolutionRefinement / 2 * (SizeRefine / 2 - 1));

                        int SizeRefineSuper = SizeRefine * SpeciesCTFSuperresFactor[Species];
                        int SizeFull = Species.HalfMap1Reconstruction[GPUID].Dims.X;
                        int SizeFullSuper = SizeFull * SpeciesCTFSuperresFactor[Species];
                        float AngPixRefine = Species.ResolutionRefinement / 2;
                        int ParticleDiameterPix = (int)(Species.DiameterAngstrom / AngPixRefine);

                        {
                            Image CoordsCTF = CTF.GetCTFCoords(SizeRefineSuper, SizeRefineSuper);   // Not SizeFullSuper because CTF creation later adjusts pixel size to AngPixRefine
                            Image CoordsCTFCropped = CTF.GetCTFCoords(SizeRefine, SizeRefine);

                            Image Extracted = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                            Image ExtractedFT = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                            Image ExtractedCropped = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                            Image ExtractedCroppedFT = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                            Image ExtractedCTF = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true, true);

                            int PlanForwSuper = GPU.CreateFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                            int PlanBackSuper = GPU.CreateIFFTPlan(new int3(SizeRefineSuper, SizeRefineSuper, 1), (uint)BatchSize);
                            int PlanForw = GPU.CreateFFTPlan(new int3(SizeRefine, SizeRefine, 1), (uint)BatchSize);

                            if (PlanForwSuper <= 0 || PlanBackSuper <= 0 || PlanForw <= 0)
                                throw new Exception("No FFT plans created!");

                            Image PhaseCorrection = CTF.GetPhaseCorrection(AngPixRefine, SizeRefineSuper);
                            Image GammaCorrection = CTF.GetGammaCorrection(AngPixRefine, SizeRefineSuper);

                            bool[] EwaldReverse = { Species.EwaldReverse, !Species.EwaldReverse };

                            for (int batchStart = 0; batchStart < NParticles; batchStart += BatchSize)
                            {
                                int CurBatch = Math.Min(BatchSize, NParticles - batchStart);
                                IEnumerable<Particle> BatchParticles = Particles.Skip(batchStart).Take(CurBatch);
                                float3[] CoordinatesMoving = Helper.Combine(BatchParticles.Select(p => p.GetCoordinateSeries(DoseInterpolationSteps)));
                                float3[] AnglesMoving = Helper.Combine(BatchParticles.Select(p => p.GetAngleSeries(DoseInterpolationSteps)));

                                for (int i = 0; i < CurBatch; i++)
                                {
                                    float3 Coords = CoordinatesMoving[i * NTilts];
                                    Coords /= VolumeDimensionsPhysical;
                                    TableOut.AddRow(new List<string>() { Coords.X.ToString(CultureInfo.InvariantCulture),
                                                                         Coords.Y.ToString(CultureInfo.InvariantCulture),
                                                                         Coords.Z.ToString(CultureInfo.InvariantCulture) });
                                }

                                for (int t = 0; t < NTilts; t++)
                                {
                                    float3[] CoordinatesTilt = new float3[CurBatch];
                                    float3[] AnglesTilt = new float3[CurBatch];
                                    for (int p = 0; p < CurBatch; p++)
                                    {
                                        CoordinatesTilt[p] = CoordinatesMoving[p * NTilts + t];
                                        AnglesTilt[p] = AnglesMoving[p * NTilts + t];
                                    }

                                    float3[] ImageCoords = GetPositionsInOneTilt(CoordinatesTilt, t);
                                    float3[] ImageAngles = GetAnglesInOneTilt(CoordinatesTilt, AnglesTilt, t);

                                    float[] Defoci = new float[CurBatch];
                                    int3[] ExtractOrigins = new int3[CurBatch];
                                    float3[] ResidualShifts = new float3[BatchSize];
                                    for (int p = 0; p < CurBatch; p++)
                                    {
                                        float ScaledX = ImageCoords[p].X / AngPixExtract, ScaledY = ImageCoords[p].Y / AngPixExtract;
                                        ExtractOrigins[p] = new int3((int)ScaledX - SizeFullSuper / 2, (int)ScaledY - SizeFullSuper / 2, 0);
                                        ResidualShifts[p] = -new float3(ScaledX - (int)ScaledX - SizeFullSuper / 2, ScaledY - (int)ScaledY - SizeFullSuper / 2, 0);
                                        Defoci[p] = ImageCoords[p].Z;
                                    }

                                    for (int iewald = 0; iewald < (Species.DoEwald ? 2 : 1); iewald++)
                                    {
                                        GPU.Extract(TiltData[t].GetDevice(Intent.Read),
                                                Extracted.GetDevice(Intent.Write),
                                                TiltData[t].Dims.Slice(),
                                                new int3(SizeFullSuper, SizeFullSuper, 1),
                                                Helper.ToInterleaved(ExtractOrigins),
                                                (uint)CurBatch);

                                        GPU.FFT(Extracted.GetDevice(Intent.Read),
                                                ExtractedFT.GetDevice(Intent.Write),
                                                new int3(SizeFullSuper, SizeFullSuper, 1),
                                                (uint)CurBatch,
                                                PlanForwSuper);

                                        ExtractedFT.ShiftSlices(ResidualShifts);
                                        ExtractedFT.Multiply(1f / (SizeFullSuper * SizeFullSuper));

                                        GPU.CropFT(ExtractedFT.GetDevice(Intent.Read),
                                                   Extracted.GetDevice(Intent.Write),
                                                   new int3(SizeFullSuper, SizeFullSuper, 1),
                                                   new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                   (uint)CurBatch);

                                        GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                           PhaseCorrection.GetDevice(Intent.Read),
                                                                           Extracted.GetDevice(Intent.Write),
                                                                           PhaseCorrection.ElementsSliceComplex,
                                                                           (uint)CurBatch);

                                        if (Species.DoEwald)
                                        {
                                            GetComplexCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, EwaldReverse[iewald], ExtractedCTF, true);

                                            GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                              ExtractedCTF.GetDevice(Intent.Read),
                                                                              ExtractedFT.GetDevice(Intent.Write),
                                                                              ExtractedCTF.ElementsComplex,
                                                                              1);
                                        }
                                        else
                                        {
                                            GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, GammaCorrection, t, ExtractedCTF, true);

                                            GPU.MultiplyComplexSlicesByScalar(Extracted.GetDevice(Intent.Read),
                                                                              ExtractedCTF.GetDevice(Intent.Read),
                                                                              ExtractedFT.GetDevice(Intent.Write),
                                                                              ExtractedCTF.ElementsComplex,
                                                                              1);
                                        }

                                        GPU.IFFT(ExtractedFT.GetDevice(Intent.Read),
                                                 Extracted.GetDevice(Intent.Write),
                                                 new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                 (uint)CurBatch,
                                                 PlanBackSuper,
                                                 false);

                                        GPU.CropFTFull(Extracted.GetDevice(Intent.Read),
                                                        ExtractedCropped.GetDevice(Intent.Write),
                                                        new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                        new int3(SizeRefine, SizeRefine, 1),
                                                        (uint)CurBatch);

                                        GPU.SphereMask(ExtractedCropped.GetDevice(Intent.Read),
                                                       ExtractedCropped.GetDevice(Intent.Write),
                                                       ExtractedCropped.Dims.Slice(),
                                                       ParticleDiameterPix / 2f,
                                                       16 * AngPixExtract / AngPixRefine,
                                                       true,
                                                       (uint)CurBatch);

                                        GPU.FFT(ExtractedCropped.GetDevice(Intent.Read),
                                                ExtractedCroppedFT.GetDevice(Intent.Write),
                                                new int3(SizeRefine, SizeRefine, 1),
                                                (uint)CurBatch,
                                                PlanForw);

                                        ExtractedCroppedFT.Multiply(1f / (SizeRefine * SizeRefine));

                                        GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTFCropped, null, t, ExtractedCTF, true, true, true);


                                        GPU.MultiParticleCorr2D(FSC.GetDeviceSlice(t * 3, Intent.ReadWrite),
                                                                new IntPtr((long)FSCPerParticle.GetDevice(Intent.ReadWrite) + (SpeciesOffset + batchStart) * FSCPerParticle.Dims.X * 3 * sizeof(float)),
                                                                PhaseResiduals.GetDevice(Intent.ReadWrite),
                                                                FSCLength,
                                                                new IntPtr[] { ExtractedCroppedFT.GetDevice(Intent.Read) },
                                                                ExtractedCTF.GetDevice(Intent.Read),
                                                                SizeRefine,
                                                                ScaleFactor,
                                                                null,
                                                                new float[CurBatch * 2],
                                                                Helper.ToInterleaved(ImageAngles),
                                                                MagnificationCorrection,
                                                                Species.DoEwald ? CTF.GetEwaldRadius(SizeFull, (float)Species.PixelSize) * (iewald == 0 ? 1 : -1) : 0,
                                                                new[] { Species.HalfMap1Projector[GPUID].t_DataRe, Species.HalfMap2Projector[GPUID].t_DataRe },
                                                                new[] { Species.HalfMap1Projector[GPUID].t_DataIm, Species.HalfMap2Projector[GPUID].t_DataIm },
                                                                Species.HalfMap1Projector[GPUID].Oversampling,
                                                                Species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                                                new IntPtr((long)SpeciesParticleSubsets[Species] + batchStart * sizeof(int)),
                                                                CurBatch,
                                                                1);
                                    }
                                }
                            }

                            PhaseCorrection.Dispose();
                            GammaCorrection.Dispose();

                            CoordsCTFCropped.Dispose();
                            CoordsCTF.Dispose();
                            Extracted.Dispose();
                            ExtractedFT.Dispose();
                            ExtractedCropped.Dispose();
                            ExtractedCroppedFT.Dispose();
                            ExtractedCTF.Dispose();

                            GPU.DestroyFFTPlan(PlanForwSuper);
                            GPU.DestroyFFTPlan(PlanBackSuper);
                            GPU.DestroyFFTPlan(PlanForw);
                        }
                    }

                    FSC.WriteMRC(System.IO.Path.Combine(workingDirectory, "..", RootName + "_fsc.mrc"), true);
                    FSC.Dispose();

                    FSCPerParticle.WriteMRC(System.IO.Path.Combine(workingDirectory, "..", RootName + "_fscparticles.mrc"), true);
                    FSCPerParticle.Dispose();

                    PhaseResiduals.WriteMRC(System.IO.Path.Combine(workingDirectory, "..", RootName + "_phaseresiduals.mrc"), true);
                    PhaseResiduals.Dispose();

                    TableOut.Save(System.IO.Path.Combine(workingDirectory, "..", RootName + "_fscparticles.star"));
                }

                #endregion

                #region Tear down

                foreach (var pair in SpeciesParticleImages)
                {
                    foreach (var ptr in SpeciesParticleImages[pair.Key])
                        GPU.FreeHostPinned(ptr);
                    if (pair.Key.DoEwald)
                        foreach (var ptr in SpeciesParticleQImages[pair.Key])
                            GPU.FreeDevice(ptr);
                    SpeciesCTFWeights[pair.Key].Dispose();
                    SpeciesTiltWeights[pair.Key].Dispose();
                    GPU.FreeDevice(SpeciesParticleSubsets[pair.Key]);

                    pair.Key.HalfMap1Projector[GPUID].FreeDevice();
                    pair.Key.HalfMap2Projector[GPUID].FreeDevice();
                }

                Console.WriteLine($"{GPU.GetFreeMemory(GPUID)} MB after optimization teardown of {Name}");

                #endregion
            }

            #region Update reconstructions with newly aligned particles

            GPU.SetDevice(GPUID);
            GPU.CheckGPUExceptions();

            progressCallback($"Extracting and back-projecting particles...");

            foreach (var species in allSpecies)
            {
                if (SpeciesParticles[species].Length == 0)
                    continue;

                Projector[] Reconstructions = { species.HalfMap1Reconstruction[GPUID], species.HalfMap2Reconstruction[GPUID] };

                float AngPixExtract = (float)OptionsDataLoad.BinnedPixelSizeMean;
                int BatchSize = optionsMPA.BatchSize;

                CTF MaxDefocusCTF = GetTiltCTF(IndicesSortedDose[0]);
                float ExpectedResolution = Math.Max((float)dataSource.PixelSizeMean * 2, (float)species.GlobalResolution * 0.8f);
                int ExpectedBoxSize = (int)(species.DiameterAngstrom / (ExpectedResolution / 2)) * 2;
                int MinimumBoxSize = Math.Max(ExpectedBoxSize, MaxDefocusCTF.GetAliasingFreeSize(ExpectedResolution));
                int CTFSuperresFactor = (int)Math.Ceiling((float)MinimumBoxSize / ExpectedBoxSize);

                int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                int SizeFullSuper = SizeFull * CTFSuperresFactor;

                Image CTFCoords = CTF.GetCTFCoords(SizeFullSuper, SizeFullSuper);
                float2[] CTFCoordsData = CTFCoords.GetHostComplexCopy()[0];
                Image CTFCoordsP = CTF.GetCTFPCoords(SizeFullSuper, SizeFullSuper);
                float2[] CTFCoordsPData = CTFCoordsP.GetHostComplexCopy()[0];
                Image CTFCoordsCropped = CTF.GetCTFCoords(SizeFull, SizeFull);

                Image GammaCorrection = CTF.GetGammaCorrection(AngPixExtract, SizeFullSuper);

                float[] PQSigns = new float[CTFCoordsData.Length];
                CTF.PrecomputePQSigns(SizeFullSuper, 2, species.EwaldReverse, CTFCoordsData, CTFCoordsPData, PQSigns);

                Image PhaseCorrection = CTF.GetPhaseCorrection(AngPixExtract, SizeFullSuper);

                Image IntermediateMaskAngles = new Image(IntPtr.Zero, new int3(SizeFull, SizeFull, 2), true);
                Image IntermediateFTCorr = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                Image IntermediateCTFP = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);

                Image MaskParticle = new Image(new int3(SizeFullSuper, SizeFullSuper, 1));
                MaskParticle.Fill(1);
                MaskParticle.MaskSpherically((float)(species.DiameterAngstrom + 6) / AngPixExtract, 3, false);
                MaskParticle.RemapToFT();

                Image Extracted = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize));
                Image ExtractedFT = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                Image ExtractedCropped = new Image(IntPtr.Zero, new int3(SizeFull, SizeFull, BatchSize));
                Image ExtractedCroppedFTp = new Image(new int3(SizeFull, SizeFull, BatchSize), true, true);
                Image ExtractedCroppedFTq = new Image(new int3(SizeFull, SizeFull, BatchSize), true, true);

                Image ExtractedCTF = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true);
                Image ExtractedCTFCropped = new Image(IntPtr.Zero, new int3(SizeFull, SizeFull, BatchSize), true);
                Image CTFWeights = new Image(IntPtr.Zero, new int3(SizeFull, SizeFull, BatchSize), true);

                int PlanForwSuper = GPU.CreateFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                int PlanBackSuper = GPU.CreateIFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                int PlanForw = GPU.CreateFFTPlan(new int3(SizeFull, SizeFull, 1), (uint)BatchSize);

                if (PlanForwSuper <= 0 || PlanBackSuper <= 0 || PlanForw <= 0)
                    throw new Exception("No FFT plans created!");

                GPU.CheckGPUExceptions();

                Particle[] AllParticles = SpeciesParticles[species];
                Particle[][] SubsetParticles = { AllParticles.Where(p => p.RandomSubset == 0).ToArray(),
                                                 AllParticles.Where(p => p.RandomSubset == 1).ToArray() };
                
                //Image CTFAvg = new Image(new int3(SizeFull, SizeFull, BatchSize), true);

                for (int isubset = 0; isubset < 2; isubset++)
                {
                    Particle[] Particles = SubsetParticles[isubset];
                    int NParticles = Particles.Length;
                    //NParticles = 1;

                    for (int batchStart = 0; batchStart < NParticles; batchStart += BatchSize)
                    {
                        int CurBatch = Math.Min(BatchSize, NParticles - batchStart);
                        IEnumerable<Particle> BatchParticles = Particles.Skip(batchStart).Take(CurBatch);
                        float3[] CoordinatesMoving = Helper.Combine(BatchParticles.Select(p => p.GetCoordinateSeries(DoseInterpolationSteps)));
                        float3[] AnglesMoving = Helper.Combine(BatchParticles.Select(p => p.GetAngleSeries(DoseInterpolationSteps)));
                        
                        for (int t = 0; t < NTilts; t++)
                        {
                            float3[] CoordinatesTilt = new float3[CurBatch];
                            float3[] AnglesTilt = new float3[CurBatch];
                            for (int p = 0; p < CurBatch; p++)
                            {
                                CoordinatesTilt[p] = CoordinatesMoving[p * NTilts + t];
                                AnglesTilt[p] = AnglesMoving[p * NTilts + t];
                            }

                            float3[] ImageCoords = GetPositionsInOneTilt(CoordinatesTilt, t);
                            float3[] ImageAngles = GetAnglesInOneTilt(CoordinatesTilt, AnglesTilt, t);

                            float[] Defoci = new float[CurBatch];
                            int3[] ExtractOrigins = new int3[CurBatch];
                            float3[] ResidualShifts = new float3[BatchSize];
                            for (int p = 0; p < CurBatch; p++)
                            {
                                float ScaledX = ImageCoords[p].X / AngPixExtract, ScaledY = ImageCoords[p].Y / AngPixExtract;
                                ExtractOrigins[p] = new int3((int)ScaledX - SizeFullSuper / 2, (int)ScaledY - SizeFullSuper / 2, 0);
                                ResidualShifts[p] = -new float3(ScaledX - (int)ScaledX - SizeFullSuper / 2, ScaledY - (int)ScaledY - SizeFullSuper / 2, 0);
                                Defoci[p] = ImageCoords[p].Z;
                            }

                            #region Image data

                            GPU.Extract(TiltData[t].GetDevice(Intent.Read),
                                        Extracted.GetDevice(Intent.Write),
                                        TiltData[t].Dims.Slice(),
                                        new int3(SizeFullSuper, SizeFullSuper, 1),
                                        Helper.ToInterleaved(ExtractOrigins),
                                        (uint)CurBatch);

                            GPU.FFT(Extracted.GetDevice(Intent.Read),
                                    ExtractedFT.GetDevice(Intent.Write),
                                    new int3(SizeFullSuper, SizeFullSuper, 1),
                                    (uint)CurBatch,
                                    PlanForwSuper);

                            ExtractedFT.ShiftSlices(ResidualShifts);
                            ExtractedFT.Multiply(1f / (SizeFullSuper * SizeFullSuper));

                            GPU.MultiplyComplexSlicesByComplex(ExtractedFT.GetDevice(Intent.Read),
                                                               PhaseCorrection.GetDevice(Intent.Read),
                                                               ExtractedFT.GetDevice(Intent.Write),
                                                               PhaseCorrection.ElementsComplex,
                                                               (uint)CurBatch);

                            CTF[] CTFParams = GetCTFParamsForOneTilt(AngPixExtract, Defoci, ImageCoords, t, false, false, false);

                            CTF.ApplyPandQPrecomp(ExtractedFT,
                                                  CTFParams,
                                                  IntermediateFTCorr,
                                                  Extracted,
                                                  ExtractedCropped,
                                                  IntermediateCTFP,
                                                  CTFCoords,
                                                  GammaCorrection,
                                                  species.EwaldReverse,
                                                  null,
                                                  PlanForw,
                                                  PlanBackSuper,
                                                  ExtractedCroppedFTp,
                                                  ExtractedCroppedFTq);

                            GetCTFsForOneTilt(AngPixExtract, Defoci, ImageCoords, CTFCoordsCropped, null, t, CTFWeights, true, true, true);

                            ExtractedCroppedFTp.Multiply(CTFWeights);
                            ExtractedCroppedFTq.Multiply(CTFWeights);

                            #endregion

                            #region CTF data

                            float[][] ExtractedCTFData = ExtractedCTF.GetHost(Intent.Write);
                            Parallel.For(0, CurBatch, i =>
                            {
                                CTFParams[i].GetEwaldWeights(CTFCoordsData, species.DiameterAngstrom, ExtractedCTFData[i]);
                            });
                            ExtractedCTF.Multiply(ExtractedCTF);

                            ExtractedFT.Fill(new float2(1, 0));
                            ExtractedFT.Multiply(ExtractedCTF);

                            GPU.IFFT(ExtractedFT.GetDevice(Intent.Read),
                                     Extracted.GetDevice(Intent.Write),
                                     new int3(SizeFullSuper, SizeFullSuper, 1),
                                     (uint)CurBatch,
                                     PlanBackSuper,
                                     false);

                            GPU.CropFTFull(Extracted.GetDevice(Intent.Read),
                                           ExtractedCropped.GetDevice(Intent.Write),
                                           new int3(SizeFullSuper, SizeFullSuper, 1),
                                           new int3(SizeFull, SizeFull, 1),
                                           (uint)CurBatch);

                            GPU.FFT(ExtractedCropped.GetDevice(Intent.Read),
                                    ExtractedFT.GetDevice(Intent.Write),
                                    new int3(SizeFull, SizeFull, 1),
                                    (uint)CurBatch,
                                    PlanForw);

                            GPU.Real(ExtractedFT.GetDevice(Intent.Read),
                                     ExtractedCTFCropped.GetDevice(Intent.Write),
                                     ExtractedCTFCropped.ElementsReal);

                            ExtractedCTFCropped.Multiply(1f / (SizeFull * SizeFull));
                            ExtractedCTFCropped.Multiply(CTFWeights);

                            #endregion
                            //ImageAngles = new[] { new float3(0, 0, 0) };
                            //ImageAngles = Helper.ArrayOfConstant(new float3(0, 0, 0), CurBatch);

                            Reconstructions[isubset].BackProject(ExtractedCroppedFTp, ExtractedCTFCropped, ImageAngles, MagnificationCorrection, CTFParams[0].GetEwaldRadius(SizeFull, (float)species.PixelSize));
                            Reconstructions[isubset].BackProject(ExtractedCroppedFTq, ExtractedCTFCropped, ImageAngles, MagnificationCorrection, -CTFParams[0].GetEwaldRadius(SizeFull, (float)species.PixelSize));
                        }
                    }
                }

                //CTFAvg.WriteMRC("d_ctfavg.mrc", true);

                //EmpiricalWeights.Dispose();

                CTFCoords.Dispose();
                CTFCoordsP.Dispose();
                CTFCoordsCropped.Dispose();
                GammaCorrection.Dispose();
                PhaseCorrection.Dispose();
                Extracted.Dispose();
                ExtractedFT.Dispose();
                ExtractedCropped.Dispose();
                ExtractedCroppedFTp.Dispose();
                ExtractedCroppedFTq.Dispose();
                ExtractedCTF.Dispose();
                ExtractedCTFCropped.Dispose();
                CTFWeights.Dispose();

                MaskParticle.Dispose();

                IntermediateMaskAngles.Dispose();
                IntermediateFTCorr.Dispose();
                IntermediateCTFP.Dispose();

                GPU.DestroyFFTPlan(PlanForwSuper);
                GPU.DestroyFFTPlan(PlanBackSuper);
                GPU.DestroyFFTPlan(PlanForw);

                species.HalfMap1Reconstruction[GPUID].FreeDevice();
                species.HalfMap2Reconstruction[GPUID].FreeDevice();
            }

            Console.WriteLine($"{GPU.GetFreeMemory(GPUID)} MB after backprojection of {Name}");

            #endregion

            for (int t = 0; t < NTilts; t++)
                TiltData[t]?.FreeDevice();

            Console.WriteLine($"{GPU.GetFreeMemory(GPUID)} MB after full refinement of {Name}");
        }

        public void PerformMultiParticleRefinementOneTiltMovie(string workingDirectory,
                                                               ProcessingOptionsMPARefine optionsMPA,
                                                               Species[] allSpecies,
                                                               DataSource dataSource,
                                                               Movie tiltMovie,
                                                               Image[] tiltMovieData,
                                                               int tiltID,
                                                               Dictionary<Species, Particle[]> SpeciesParticles,
                                                               Dictionary<Species, IntPtr> SpeciesParticleSubsets,
                                                               Dictionary<Species, (int Start, int End)> SpeciesParticleIDRanges,
                                                               Dictionary<Species, int> SpeciesRefinementSize,
                                                               Dictionary<Species, int[]> SpeciesRelevantRefinementSizes,
                                                               Dictionary<Species, Image> SpeciesFrameWeights,
                                                               Dictionary<Species, int> SpeciesCTFSuperresFactor)
        {
            int GPUID = GPU.GetDevice();
            HeaderEER.GroupNFrames = dataSource.EERGroupFrames;
            NFrames = MapHeader.ReadFromFile(tiltMovie.Path).Dimensions.Z;
            //NFrames = 1;
            FractionFrames = 1;

            if (true)
            {
                #region Resize grids

                if (tiltMovie.PyramidShiftX == null || tiltMovie.PyramidShiftX.Count == 0 || tiltMovie.PyramidShiftX[0].Dimensions.Z != NFrames)
                {
                    tiltMovie.PyramidShiftX = new List<CubicGrid>();
                    tiltMovie.PyramidShiftY = new List<CubicGrid>();

                    tiltMovie.PyramidShiftX.Add(new CubicGrid(new int3(1, 1, NFrames)));
                    tiltMovie.PyramidShiftX.Add(new CubicGrid(new int3(3, 3, 3)));

                    tiltMovie.PyramidShiftY.Add(new CubicGrid(new int3(1, 1, NFrames)));
                    tiltMovie.PyramidShiftY.Add(new CubicGrid(new int3(3, 3, 3)));
                }

                #endregion

                #region Figure out dimensions

                tiltMovie.ImageDimensionsPhysical = new float2(new int2(MapHeader.ReadFromFile(tiltMovie.Path).Dimensions)) * (float)dataSource.PixelSizeMean;

                float MinDose = MathHelper.Min(Dose), MaxDose = MathHelper.Max(Dose);
                float TiltInterpolationCoord = (Dose[tiltID] - MinDose) / (MaxDose - MinDose);

                float SmallestAngPix = MathHelper.Min(allSpecies.Select(s => (float)s.PixelSize));
                float LargestBox = MathHelper.Max(allSpecies.Select(s => s.DiameterAngstrom)) * 2 / SmallestAngPix;

                decimal BinTimes = (decimal)Math.Log(SmallestAngPix / (float)dataSource.PixelSizeMean, 2.0);
                ProcessingOptionsTomoSubReconstruction OptionsDataLoad = new ProcessingOptionsTomoSubReconstruction()
                {
                    PixelSizeX = dataSource.PixelSizeX,
                    PixelSizeY = dataSource.PixelSizeY,
                    PixelSizeAngle = dataSource.PixelSizeAngle,

                    BinTimes = BinTimes,
                    GainPath = dataSource.GainPath,
                    GainHash = "",
                    GainFlipX = dataSource.GainFlipX,
                    GainFlipY = dataSource.GainFlipY,
                    GainTranspose = dataSource.GainTranspose,
                    DefectsPath = dataSource.DefectsPath,
                    DefectsHash = "",

                    Invert = true,
                    NormalizeInput = true,
                    NormalizeOutput = false,

                    PrerotateParticles = true
                };

                #endregion

                foreach (var frame in tiltMovieData)
                {
                    frame.Bandpass(1f / LargestBox, 1f, false, 0f);
                    frame.Multiply(-1);
                }

                #region Extract particles

                Dictionary<Species, IntPtr[]> SpeciesParticleImages = new Dictionary<Species, IntPtr[]>();
                Dictionary<Species, float2[]> SpeciesParticleExtractedAt = new Dictionary<Species, float2[]>();

                int NParticlesOverall = 0;

                foreach (var species in allSpecies)
                {
                    if (SpeciesParticles[species].Length == 0)
                        continue;

                    Particle[] Particles = SpeciesParticles[species];
                    int NParticles = Particles.Length;
                    NParticlesOverall += NParticles;

                    int Size = SpeciesRelevantRefinementSizes[species][tiltID];// species.HalfMap1Projector[GPUID].Dims.X;
                    long ElementsSliceComplex = (Size / 2 + 1) * Size;

                    SpeciesParticleImages.Add(species, Helper.ArrayOfFunction(i => GPU.MallocHostPinned((new int3(Size).Slice().ElementsFFT()) * 2 * (long)NParticles), NFrames));
                    SpeciesParticleExtractedAt.Add(species, new float2[NParticles * NFrames]);
                }

                #endregion

                #region Helper functions

                Action<bool> ReextractPaddedParticles = (CorrectBeamTilt) =>
                {
                    float AngPixExtract = (float)OptionsDataLoad.BinnedPixelSizeMean;
                    int BatchSize = optionsMPA.BatchSize;

                    foreach (var species in allSpecies)
                    {
                        if (!SpeciesParticles.ContainsKey(species))
                            continue;

                        Particle[] Particles = SpeciesParticles[species];
                        int NParticles = Particles.Length;

                        int SizeRelevant = SpeciesRelevantRefinementSizes[species][tiltID];
                        int SizeRefine = species.HalfMap1Projector[GPUID].Dims.X;
                        int SizeRefineSuper = SizeRefine * SpeciesCTFSuperresFactor[species];
                        int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;
                        int SizeFullSuper = SizeFull * SpeciesCTFSuperresFactor[species];
                        float AngPixRefine = species.ResolutionRefinement / 2;
                        int ParticleDiameterPix = (int)(species.DiameterAngstrom / AngPixRefine);

                        float2[] ExtractedAt = SpeciesParticleExtractedAt[species];

                        Image CoordsCTF = CTF.GetCTFCoords(SizeRefineSuper, SizeRefineSuper);
                        Image BeamTiltCorrection = CTF.GetBeamTilt(SizeRefineSuper, SizeFullSuper);

                        Image Extracted = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                        Image ExtractedFT = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                        Image ExtractedCropped = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize));
                        Image ExtractedCroppedFT = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                        Image ExtractedCroppedFTRelevantSize = new Image(IntPtr.Zero, new int3(SizeRelevant, SizeRelevant, BatchSize), true, true);
                        Image ExtractedCTF = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true);

                        Image CTFFrameWeights = tiltMovie.GetCTFsForOneParticle(OptionsDataLoad, new float3(0, 0, 0), CoordsCTF, null, true, true);

                        //Image SumAll = new Image(new int3(SizeRefine, SizeRefine, BatchSize));

                        int PlanForwSuper = GPU.CreateFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                        int PlanBackSuper = GPU.CreateIFFTPlan(new int3(SizeRefineSuper, SizeRefineSuper, 1), (uint)BatchSize);
                        int PlanForw = GPU.CreateFFTPlan(new int3(SizeRefine, SizeRefine, 1), (uint)BatchSize);


                        for (int batchStart = 0; batchStart < NParticles; batchStart += BatchSize)
                        {
                            int CurBatch = Math.Min(BatchSize, NParticles - batchStart);
                            IEnumerable<Particle> BatchParticles = Particles.Skip(batchStart).Take(CurBatch);
                            float3[] CoordinatesMoving = BatchParticles.Select(p => p.GetCoordinatesAt(TiltInterpolationCoord)).ToArray();

                            float3[] CoordinatesTilt = GetPositionsInOneTilt(CoordinatesMoving, tiltID);

                            for (int f = 0; f < NFrames; f++)
                            {
                                float3[] ImageCoords = tiltMovie.GetPositionsInOneFrame(CoordinatesTilt, f);

                                float[] Defoci = new float[CurBatch];
                                int3[] ExtractOrigins = new int3[CurBatch];
                                float3[] ResidualShifts = new float3[BatchSize];
                                for (int p = 0; p < CurBatch; p++)
                                {
                                    float ScaledX = ImageCoords[p].X / AngPixExtract, ScaledY = ImageCoords[p].Y / AngPixExtract;
                                    ExtractOrigins[p] = new int3((int)ScaledX - SizeFullSuper / 2, (int)ScaledY - SizeFullSuper / 2, 0);
                                    ResidualShifts[p] = -new float3(ScaledX - (int)ScaledX - SizeFullSuper / 2, ScaledY - (int)ScaledY - SizeFullSuper / 2, 0);
                                    Defoci[p] = CoordinatesTilt[p].Z;
                                    ExtractedAt[(batchStart + p) * NFrames + f] = new float2(ImageCoords[p]);
                                }

                                GPU.Extract(tiltMovieData[f].GetDevice(Intent.Read),
                                            Extracted.GetDevice(Intent.Write),
                                            tiltMovieData[f].Dims.Slice(),
                                            new int3(SizeFullSuper, SizeFullSuper, 1),
                                            Helper.ToInterleaved(ExtractOrigins),
                                            (uint)CurBatch);

                                GPU.FFT(Extracted.GetDevice(Intent.Read),
                                        ExtractedFT.GetDevice(Intent.Write),
                                        new int3(SizeFullSuper, SizeFullSuper, 1),
                                        (uint)CurBatch,
                                        PlanForwSuper);

                                ExtractedFT.ShiftSlices(ResidualShifts);
                                ExtractedFT.Multiply(1f / (SizeFullSuper * SizeFullSuper));

                                GPU.CropFT(ExtractedFT.GetDevice(Intent.Read),
                                           Extracted.GetDevice(Intent.Write),
                                           new int3(SizeFullSuper, SizeFullSuper, 1),
                                           new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                           (uint)CurBatch);

                                GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, null, tiltID, ExtractedCTF, true);

                                if (CorrectBeamTilt)
                                    GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                       BeamTiltCorrection.GetDevice(Intent.Read),
                                                                       Extracted.GetDevice(Intent.Write),
                                                                       BeamTiltCorrection.ElementsSliceComplex,
                                                                       (uint)CurBatch);

                                GPU.MultiplyComplexSlicesByScalar(Extracted.GetDevice(Intent.Read),
                                                                  ExtractedCTF.GetDevice(Intent.Read),
                                                                  Extracted.GetDevice(Intent.Write),
                                                                  ExtractedCTF.ElementsReal,
                                                                  1);

                                GPU.MultiplyComplexSlicesByScalar(Extracted.GetDevice(Intent.Read),
                                                                  CTFFrameWeights.GetDeviceSlice(f, Intent.Read),
                                                                  ExtractedFT.GetDevice(Intent.Write),
                                                                  CTFFrameWeights.ElementsSliceReal,
                                                                  (uint)CurBatch);

                                GPU.IFFT(ExtractedFT.GetDevice(Intent.Read),
                                         Extracted.GetDevice(Intent.Write),
                                         new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                         (uint)CurBatch,
                                         PlanBackSuper,
                                         false);

                                GPU.CropFTFull(Extracted.GetDevice(Intent.Read),
                                                ExtractedCropped.GetDevice(Intent.Write),
                                                new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                new int3(SizeRefine, SizeRefine, 1),
                                                (uint)CurBatch);

                                GPU.SphereMask(ExtractedCropped.GetDevice(Intent.Read),
                                               ExtractedCropped.GetDevice(Intent.Write),
                                               ExtractedCropped.Dims.Slice(),
                                               ParticleDiameterPix / 2f,
                                               16 * AngPixExtract / AngPixRefine,
                                               true,
                                               (uint)CurBatch);

                                //SumAll.Add(ExtractedCropped);

                                GPU.FFT(ExtractedCropped.GetDevice(Intent.Read),
                                        ExtractedCroppedFT.GetDevice(Intent.Write),
                                        new int3(SizeRefine, SizeRefine, 1),
                                        (uint)CurBatch,
                                        PlanForw);

                                ExtractedCroppedFT.Multiply(1f / (SizeRefine * SizeRefine));

                                GPU.CropFT(ExtractedCroppedFT.GetDevice(Intent.Read),
                                           ExtractedCroppedFTRelevantSize.GetDevice(Intent.Write),
                                           new int3(SizeRefine).Slice(),
                                           new int3(SizeRelevant).Slice(),
                                           (uint)CurBatch);

                                GPU.CopyDeviceToHostPinned(ExtractedCroppedFTRelevantSize.GetDevice(Intent.Read),
                                                           new IntPtr((long)SpeciesParticleImages[species][f] + (new int3(SizeRelevant).Slice().ElementsFFT()) * 2 * batchStart * sizeof(float)),
                                                           (new int3(SizeRelevant).Slice().ElementsFFT()) * 2 * CurBatch);
                            }
                        }

                        //SumAll.AsReducedAlongZ().WriteMRC("d_sumall.mrc", true);
                        //SumAll.Dispose();

                        CTFFrameWeights.Dispose();

                        CoordsCTF.Dispose();
                        Extracted.Dispose();
                        ExtractedFT.Dispose();
                        ExtractedCropped.Dispose();
                        ExtractedCroppedFT.Dispose();
                        ExtractedCroppedFTRelevantSize.Dispose();
                        ExtractedCTF.Dispose();

                        GPU.DestroyFFTPlan(PlanForwSuper);
                        GPU.DestroyFFTPlan(PlanBackSuper);
                        GPU.DestroyFFTPlan(PlanForw);
                    }
                };

                Func<float2, float[]> GetRawCC = (shiftBias) =>
                {
                    float[] Result = new float[NParticlesOverall * NFrames * 3];

                    for (int ispecies = 0; ispecies < allSpecies.Length; ispecies++)
                    {
                        Species Species = allSpecies[ispecies];
                        Particle[] Particles = SpeciesParticles[Species];

                        int NParticles = Particles.Length;
                        float SpeciesAngPix = Species.ResolutionRefinement / 2;
                        if (NParticles == 0)
                            continue;

                        float[] SpeciesResult = new float[NParticles * NFrames * 3];

                        float3[] ParticlePositions = new float3[NParticles * NFrames];
                        float3[] ParticleAngles = new float3[NParticles * NFrames];
                        for (int p = 0; p < NParticles; p++)
                        {
                            float3 Position = Particles[p].GetCoordinatesAt(TiltInterpolationCoord);
                            float3 Angles = Particles[p].GetAnglesAt(TiltInterpolationCoord);

                            for (int f = 0; f < NFrames; f++)
                            {
                                ParticlePositions[p * NFrames + f] = Position;
                                ParticleAngles[p * NFrames + f] = Angles;
                            }
                        }

                        float3[] ParticlePositionsTilt = GetPositionsInOneTilt(ParticlePositions, tiltID);

                        float3[] ParticlePositionsProjected = tiltMovie.GetPositionInAllFrames(ParticlePositionsTilt);
                        float3[] ParticleAnglesInFrames = GetAnglesInOneTilt(ParticlePositions, ParticleAngles, tiltID);

                        float2[] ParticleExtractedAt = SpeciesParticleExtractedAt[Species];
                        float2[] ParticleShifts = new float2[NFrames * NParticles];
                        for (int p = 0; p < NParticles; p++)
                            for (int t = 0; t < NFrames; t++)
                                ParticleShifts[p * NFrames + t] = (new float2(ParticlePositionsProjected[p * NFrames + t]) - ParticleExtractedAt[p * NFrames + t] + shiftBias) / SpeciesAngPix;

                        int SizeRelevant = SpeciesRelevantRefinementSizes[Species][tiltID];
                        int SizeRefine = Species.HalfMap1Projector[GPUID].Dims.X;
                        int SizeFull = Species.HalfMap1Reconstruction[GPUID].Dims.X;

                        Image PhaseCorrection = CTF.GetBeamTilt(SizeRefine, SizeFull);
                        float2[] BeamTilts = Helper.ArrayOfConstant(CTF.BeamTilt, NParticles);
                        Image PhaseCorrectionAll = new Image(new int3(SizeRefine, SizeRefine, NFrames), true, true);
                        for (int t = 0; t < NFrames; t++)
                            GPU.CropFT(PhaseCorrection.GetDevice(Intent.Read),
                                        PhaseCorrectionAll.GetDeviceSlice(t, Intent.Write),
                                        PhaseCorrection.Dims.Slice(),
                                        new int3(SizeRelevant).Slice(),
                                        1);

                        GPU.MultiParticleDiff(SpeciesResult,
                                              SpeciesParticleImages[Species],
                                              SpeciesRefinementSize[Species],
                                              Helper.ArrayOfConstant(SizeRelevant, NFrames),
                                              Helper.ToInterleaved(ParticleShifts),
                                              Helper.ToInterleaved(ParticleAnglesInFrames),
                                              MagnificationCorrection,
                                              SpeciesFrameWeights[Species].GetDevice(Intent.ReadWrite),
                                              PhaseCorrectionAll.GetDevice(Intent.Read),
                                              0,
                                              Species.CurrentMaxShellRefinement,
                                              new[] { Species.HalfMap1Projector[GPUID].t_DataRe, Species.HalfMap2Projector[GPUID].t_DataRe },
                                              new[] { Species.HalfMap1Projector[GPUID].t_DataIm, Species.HalfMap2Projector[GPUID].t_DataIm },
                                              Species.HalfMap1Projector[GPUID].Oversampling,
                                              Species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                              SpeciesParticleSubsets[Species],
                                              NParticles,
                                              NFrames);

                        PhaseCorrectionAll.Dispose();
                        PhaseCorrection.Dispose();

                        int Offset = SpeciesParticleIDRanges[Species].Start * NFrames * 3;
                        Array.Copy(SpeciesResult, 0, Result, Offset, SpeciesResult.Length);
                    }

                    return Result;
                };

                Func<(float[] xp, float[] xm, float[] yp, float[] ym, float delta2)> GetRawShiftGradients = () =>
                {
                    float Delta = 0.025f;
                    float Delta2 = Delta * 2;

                    float[] h_ScoresXP = GetRawCC(float2.UnitX * Delta);
                    float[] h_ScoresXM = GetRawCC(-float2.UnitX * Delta);
                    float[] h_ScoresYP = GetRawCC(float2.UnitY * Delta);
                    float[] h_ScoresYM = GetRawCC(-float2.UnitY * Delta);

                    //for (int i = 0; i < Result.Length; i++)
                    //    Result[i] = new float2((h_ScoresXP[i] - h_ScoresXM[i]) / Delta2 * 100,
                    //                           (h_ScoresYP[i] - h_ScoresYM[i]) / Delta2 * 100);

                    return (h_ScoresXP, h_ScoresXM, h_ScoresYP, h_ScoresYM, Delta2);
                };

                Func<double[]> GetPerFrameDiff2 = () =>
                {
                    double[] Result = new double[NFrames * 3];
                    float[] RawResult = GetRawCC(new float2(0));

                    for (int p = 0; p < NParticlesOverall; p++)
                        for (int f = 0; f < NFrames; f++)
                        {
                            Result[f * 3 + 0] += RawResult[(p * NFrames + f) * 3 + 0];
                            Result[f * 3 + 1] += RawResult[(p * NFrames + f) * 3 + 1];
                            Result[f * 3 + 2] += RawResult[(p * NFrames + f) * 3 + 2];
                        }

                    Result = Helper.ArrayOfFunction(t => Result[t * 3 + 0] / Math.Max(1e-10, Math.Sqrt(Result[t * 3 + 1] * Result[t * 3 + 2])) * 100 * NParticlesOverall, NFrames);

                    return Result;
                };

                #endregion

                ReextractPaddedParticles(false);

                float2[][] OriginalOffsets = Helper.ArrayOfFunction(p => Helper.ArrayOfFunction(t => new float2(tiltMovie.PyramidShiftX[p].Values[t],
                                                                                                                tiltMovie.PyramidShiftY[p].Values[t]),
                                                                                                tiltMovie.PyramidShiftX[p].Values.Length),
                                                                    tiltMovie.PyramidShiftX.Count);

                int BFGSIterations = 0;

                double[] InitialParametersWarp = new double[tiltMovie.PyramidShiftX.Select(g => g.Values.Length * 2).Sum()];

                #region Set parameters from vector

                Action<double[], Movie> SetWarpFromVector = (input, movie) =>
                {
                    int Offset = 0;

                    int3[] PyramidDimensions = tiltMovie.PyramidShiftX.Select(g => g.Dimensions).ToArray();

                    movie.PyramidShiftX.Clear();
                    movie.PyramidShiftY.Clear();

                    for (int p = 0; p < PyramidDimensions.Length; p++)
                    {
                        float[] MovementXData = new float[PyramidDimensions[p].Elements()];
                        float[] MovementYData = new float[PyramidDimensions[p].Elements()];
                        for (int i = 0; i < MovementXData.Length; i++)
                        {
                            MovementXData[i] = OriginalOffsets[p][i].X + (float)input[Offset + i];
                            MovementYData[i] = OriginalOffsets[p][i].Y + (float)input[Offset + MovementXData.Length + i];
                        }
                        movie.PyramidShiftX.Add(new CubicGrid(PyramidDimensions[p], MovementXData));
                        movie.PyramidShiftY.Add(new CubicGrid(PyramidDimensions[p], MovementYData));

                        Offset += MovementXData.Length * 2;
                    }
                };

                #endregion

                #region Wiggle weights

                int NWiggleDifferentiable = tiltMovie.PyramidShiftX.Select(g => g.Values.Length * 2).Sum();
                (int[] indices, float2[] weights)[] AllWiggleWeights = new (int[] indices, float2[] weights)[NWiggleDifferentiable];

                {
                    Movie[] ParallelMovieCopies = Helper.ArrayOfFunction(i => new Movie(tiltMovie.Path), 32);
                    Dictionary<Species, float3[]> SpeciesParticlePositions = new Dictionary<Species, float3[]>();
                    foreach (var species in allSpecies)
                    {
                        Particle[] Particles = SpeciesParticles[species];
                        int NParticles = Particles.Length;
                        if (NParticles == 0)
                            continue;

                        float3[] ParticlePositions = new float3[NParticles * NFrames];
                        for (int p = 0; p < NParticles; p++)
                        {
                            float3 Position = Particles[p].GetCoordinatesAt(TiltInterpolationCoord);

                            for (int f = 0; f < NFrames; f++)
                                ParticlePositions[p * NFrames + f] = Position;
                        }

                        float3[] ParticlePositionsTilt = GetPositionsInOneTilt(ParticlePositions, tiltID);
                        SpeciesParticlePositions.Add(species, ParticlePositionsTilt);
                    }

                    Helper.ForCPU(0, NWiggleDifferentiable, ParallelMovieCopies.Length, (threadID) =>
                    {
                        ParallelMovieCopies[threadID].ImageDimensionsPhysical = ImageDimensionsPhysical;
                        ParallelMovieCopies[threadID].NFrames = NFrames;
                        ParallelMovieCopies[threadID].FractionFrames = FractionFrames;
                    },
                    (iwiggle, threadID) =>
                    {
                        double[] WiggleParams = new double[InitialParametersWarp.Length];
                        WiggleParams[iwiggle] = 1;
                        SetWarpFromVector(WiggleParams, ParallelMovieCopies[threadID]);

                        float2[] RawShifts = new float2[NParticlesOverall * NFrames];
                        foreach (var species in allSpecies)
                        {
                            Particle[] Particles = SpeciesParticles[species];
                            int NParticles = Particles.Length;
                            if (NParticles == 0)
                                continue;

                            int Offset = SpeciesParticleIDRanges[species].Start;

                            float3[] ParticlePositionsProjected = ParallelMovieCopies[threadID].GetPositionInAllFrames(SpeciesParticlePositions[species]);
                            float2[] ParticleExtractedAt = SpeciesParticleExtractedAt[species];

                            for (int p = 0; p < NParticles; p++)
                                for (int f = 0; f < NFrames; f++)
                                    RawShifts[(Offset + p) * NFrames + f] = (new float2(ParticlePositionsProjected[p * NFrames + f]) - ParticleExtractedAt[p * NFrames + f]);
                        }

                        List<int> Indices = new List<int>(RawShifts.Length / 5);
                        List<float2> Weights = new List<float2>(RawShifts.Length / 5);
                        for (int i = 0; i < RawShifts.Length; i++)
                        {
                            if (RawShifts[i].LengthSq() > 1e-6f)
                            {
                                Indices.Add(i);
                                Weights.Add(RawShifts[i]);

                                if (Math.Abs(RawShifts[i].X) > 1.05f)
                                    throw new Exception();
                            }
                        }

                        AllWiggleWeights[iwiggle] = (Indices.ToArray(), Weights.ToArray());
                    }, null);
                }

                #endregion

                #region Loss and gradient functions for warping

                Func<double[], double> WarpEval = input =>
                {
                    SetWarpFromVector(input, tiltMovie);

                    double[] TiltScores = GetPerFrameDiff2();
                    double Score = TiltScores.Sum();

                    Console.WriteLine(Score);

                    return Score;
                };

                Func<double[], double[]> WarpGrad = input =>
                {
                    double[] Result = new double[input.Length];

                    if (++BFGSIterations >= 12)
                        return Result;

                    SetWarpFromVector(input, tiltMovie);
                    (var XP, var XM, var YP, var YM, var Delta2Movement) = GetRawShiftGradients();

                    Parallel.For(0, AllWiggleWeights.Length, iwiggle =>
                    {
                        double SumGrad = 0;
                        double SumWeights = 0;
                        double SumWeightsGrad = 0;

                        int[] Indices = AllWiggleWeights[iwiggle].indices;
                        float2[] Weights = AllWiggleWeights[iwiggle].weights;

                        for (int i = 0; i < Indices.Length; i++)
                        {
                            int id = Indices[i];

                            SumWeights += Math.Abs(Weights[i].X) * Math.Sqrt(XP[id * 3 + 1] + XM[id * 3 + 1]) +
                                          Math.Abs(Weights[i].Y) * Math.Sqrt(YP[id * 3 + 1] + YM[id * 3 + 1]);
                            SumWeightsGrad += Math.Abs(Weights[i].X) + Math.Abs(Weights[i].Y);

                            double GradX = (XP[id * 3 + 0] / Math.Max(1e-15, Math.Sqrt(XP[id * 3 + 1] * XP[id * 3 + 2])) -
                                            XM[id * 3 + 0] / Math.Max(1e-15, Math.Sqrt(XM[id * 3 + 1] * XM[id * 3 + 2]))) / Delta2Movement;
                            double GradY = (YP[id * 3 + 0] / Math.Max(1e-15, Math.Sqrt(YP[id * 3 + 1] * YP[id * 3 + 2])) -
                                            YM[id * 3 + 0] / Math.Max(1e-15, Math.Sqrt(YM[id * 3 + 1] * YM[id * 3 + 2]))) / Delta2Movement;

                            SumGrad += Weights[i].X * Math.Sqrt(XP[id * 3 + 1] + XM[id * 3 + 1]) * GradX;
                            SumGrad += Weights[i].Y * Math.Sqrt(YP[id * 3 + 1] + YM[id * 3 + 1]) * GradY;
                        }

                        Result[iwiggle] = SumGrad / Math.Max(1e-15, SumWeights) * 100 * SumWeightsGrad;
                    });

                    return Result;
                };

                #endregion


                foreach (var species in allSpecies)
                    species.CurrentMaxShellRefinement = species.HalfMap1Projector[GPUID].Dims.X / 2;

                BroydenFletcherGoldfarbShanno OptimizerWarp = new BroydenFletcherGoldfarbShanno(InitialParametersWarp.Length, WarpEval, WarpGrad);

                SetWarpFromVector(InitialParametersWarp, tiltMovie);

                BFGSIterations = 0;
                OptimizerWarp.Maximize(InitialParametersWarp);

                SetWarpFromVector(InitialParametersWarp, tiltMovie);

                #region Compute FSC between refs and particles to estimate frame and micrograph weights

                if (false)
                {
                    int FSCLength = 64;
                    Image FSC = new Image(new int3(FSCLength, FSCLength, NFrames * 3), true);
                    Image FSCPerParticle = new Image(new int3(FSCLength / 2, NParticlesOverall * 3, 1));
                    //float[][] FSCData = FSC.GetHost(Intent.ReadWrite);
                    Image PhaseResiduals = new Image(new int3(FSCLength, FSCLength, 2), true);

                    Star TableOut = new Star(new string[] { "wrpNormCoordinateX", "wrpNormCoordinateY", "wrpNormCoordinateZ" });

                    int BatchSize = optionsMPA.BatchSize;
                    float AngPixExtract = (float)OptionsDataLoad.BinnedPixelSizeMean;

                    for (int ispecies = 0; ispecies < allSpecies.Length; ispecies++)
                    {
                        Species Species = allSpecies[ispecies];
                        Particle[] Particles = SpeciesParticles[Species];

                        int NParticles = Particles.Length;
                        float SpeciesAngPix = Species.ResolutionRefinement / 2;
                        if (NParticles == 0)
                            continue;

                        int SpeciesOffset = SpeciesParticleIDRanges[Species].Start;

                        int SizeRefine = SpeciesRefinementSize[Species];
                        int[] RelevantSizes = SpeciesRelevantRefinementSizes[Species];

                        float ScaleFactor = (float)Species.PixelSize * (FSCLength / 2 - 1) /
                                            (float)(Species.ResolutionRefinement / 2 * (SizeRefine / 2 - 1));

                        {
                            int SizeRefineSuper = SizeRefine * SpeciesCTFSuperresFactor[Species];
                            int SizeFull = Species.HalfMap1Reconstruction[GPUID].Dims.X;
                            int SizeFullSuper = SizeFull * SpeciesCTFSuperresFactor[Species];
                            float AngPixRefine = Species.ResolutionRefinement / 2;
                            int ParticleDiameterPix = (int)(Species.DiameterAngstrom / AngPixRefine);

                            Image CoordsCTF = CTF.GetCTFCoords(SizeRefineSuper, SizeRefineSuper);   // Not SizeFullSuper because CTF creation later adjusts pixel size to AngPixRefine
                            Image CoordsCTFCropped = CTF.GetCTFCoords(SizeRefine, SizeRefine);

                            Image Extracted = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                            Image ExtractedFT = new Image(IntPtr.Zero, new int3(SizeFullSuper, SizeFullSuper, BatchSize), true, true);
                            Image ExtractedCropped = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                            Image ExtractedCroppedFT = new Image(IntPtr.Zero, new int3(SizeRefine, SizeRefine, BatchSize), true, true);
                            Image ExtractedCTF = new Image(IntPtr.Zero, new int3(SizeRefineSuper, SizeRefineSuper, BatchSize), true);

                            int PlanForwSuper = GPU.CreateFFTPlan(new int3(SizeFullSuper, SizeFullSuper, 1), (uint)BatchSize);
                            int PlanBackSuper = GPU.CreateIFFTPlan(new int3(SizeRefineSuper, SizeRefineSuper, 1), (uint)BatchSize);
                            int PlanForw = GPU.CreateFFTPlan(new int3(SizeRefine, SizeRefine, 1), (uint)BatchSize);

                            if (PlanForwSuper <= 0 || PlanBackSuper <= 0 || PlanForw <= 0)
                                throw new Exception("No FFT plans created!");

                            Image BeamTiltCorrection = CTF.GetBeamTilt(SizeRefineSuper, SizeFullSuper);

                            for (int batchStart = 0; batchStart < NParticles; batchStart += BatchSize)
                            {
                                int CurBatch = Math.Min(BatchSize, NParticles - batchStart);
                                IEnumerable<Particle> BatchParticles = Particles.Skip(batchStart).Take(CurBatch);
                                float3[] CoordinatesMoving = BatchParticles.Select(p => p.GetCoordinatesAt(TiltInterpolationCoord)).ToArray();
                                float3[] AnglesMoving = BatchParticles.Select(p => p.GetAnglesAt(TiltInterpolationCoord)).ToArray();

                                float3[] CoordinatesTilt = GetPositionsInOneTilt(CoordinatesMoving, tiltID);
                                float3[] ParticleAnglesInFrames = GetAnglesInOneTilt(CoordinatesMoving, AnglesMoving, tiltID);

                                for (int i = 0; i < CurBatch; i++)
                                {
                                    float3 Coords = new float3(CoordinatesMoving[i].X, CoordinatesMoving[i].Y, CoordinatesMoving[i].Z);
                                    Coords /= VolumeDimensionsPhysical;
                                    TableOut.AddRow(new List<string>() { Coords.X.ToString(CultureInfo.InvariantCulture),
                                                                         Coords.Y.ToString(CultureInfo.InvariantCulture),
                                                                         Coords.Z.ToString(CultureInfo.InvariantCulture)});
                                }

                                for (int f = 0; f < NFrames; f++)
                                {
                                    float3[] ImageCoords = tiltMovie.GetPositionsInOneFrame(CoordinatesTilt, f);

                                    float[] Defoci = new float[CurBatch];
                                    int3[] ExtractOrigins = new int3[CurBatch];
                                    float3[] ResidualShifts = new float3[BatchSize];
                                    for (int p = 0; p < CurBatch; p++)
                                    {
                                        float ScaledX = ImageCoords[p].X / AngPixExtract, ScaledY = ImageCoords[p].Y / AngPixExtract;
                                        ExtractOrigins[p] = new int3((int)ScaledX - SizeFullSuper / 2, (int)ScaledY - SizeFullSuper / 2, 0);
                                        ResidualShifts[p] = -new float3(ScaledX - (int)ScaledX - SizeFullSuper / 2, ScaledY - (int)ScaledY - SizeFullSuper / 2, 0);
                                        Defoci[p] = CoordinatesTilt[p].Z;
                                    }

                                    GPU.Extract(tiltMovieData[f].GetDevice(Intent.Read),
                                                Extracted.GetDevice(Intent.Write),
                                                tiltMovieData[f].Dims.Slice(),
                                                new int3(SizeFullSuper, SizeFullSuper, 1),
                                                Helper.ToInterleaved(ExtractOrigins),
                                                (uint)CurBatch);

                                    GPU.FFT(Extracted.GetDevice(Intent.Read),
                                            ExtractedFT.GetDevice(Intent.Write),
                                            new int3(SizeFullSuper, SizeFullSuper, 1),
                                            (uint)CurBatch,
                                            PlanForwSuper);

                                    ExtractedFT.ShiftSlices(ResidualShifts);
                                    ExtractedFT.Multiply(1f / (SizeFullSuper * SizeFullSuper));

                                    GPU.CropFT(ExtractedFT.GetDevice(Intent.Read),
                                               Extracted.GetDevice(Intent.Write),
                                               new int3(SizeFullSuper, SizeFullSuper, 1),
                                               new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                               (uint)CurBatch);

                                    GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTF, null, tiltID, ExtractedCTF, true);

                                    GPU.MultiplyComplexSlicesByComplex(Extracted.GetDevice(Intent.Read),
                                                                       BeamTiltCorrection.GetDevice(Intent.Read),
                                                                       Extracted.GetDevice(Intent.Write),
                                                                       BeamTiltCorrection.ElementsSliceComplex,
                                                                       (uint)CurBatch);

                                    GPU.MultiplyComplexSlicesByScalar(Extracted.GetDevice(Intent.Read),
                                                                      ExtractedCTF.GetDevice(Intent.Read),
                                                                      ExtractedFT.GetDevice(Intent.Write),
                                                                      ExtractedCTF.ElementsReal,
                                                                      1);

                                    GPU.IFFT(ExtractedFT.GetDevice(Intent.Read),
                                             Extracted.GetDevice(Intent.Write),
                                             new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                             (uint)CurBatch,
                                             PlanBackSuper,
                                             false);

                                    GPU.CropFTFull(Extracted.GetDevice(Intent.Read),
                                                    ExtractedCropped.GetDevice(Intent.Write),
                                                    new int3(SizeRefineSuper, SizeRefineSuper, 1),
                                                    new int3(SizeRefine, SizeRefine, 1),
                                                    (uint)CurBatch);

                                    GPU.SphereMask(ExtractedCropped.GetDevice(Intent.Read),
                                                   ExtractedCropped.GetDevice(Intent.Write),
                                                   ExtractedCropped.Dims.Slice(),
                                                   ParticleDiameterPix / 2f,
                                                   16 * AngPixExtract / AngPixRefine,
                                                   true,
                                                   (uint)CurBatch);

                                    //SumAll.Add(ExtractedCropped);

                                    GPU.FFT(ExtractedCropped.GetDevice(Intent.Read),
                                            ExtractedCroppedFT.GetDevice(Intent.Write),
                                            new int3(SizeRefine, SizeRefine, 1),
                                            (uint)CurBatch,
                                            PlanForw);

                                    ExtractedCroppedFT.Multiply(1f / (SizeRefine * SizeRefine));

                                    GetCTFsForOneTilt(AngPixRefine, Defoci, CoordinatesTilt, CoordsCTFCropped, null, tiltID, ExtractedCTF, true, true, true);


                                    //GPU.MultiParticleCorr2D(CorrAB.GetDeviceSlice(f, Intent.ReadWrite),
                                    //                        CorrA2.GetDeviceSlice(f, Intent.ReadWrite),
                                    //                        CorrB2.GetDeviceSlice(f, Intent.ReadWrite),
                                    //                        new IntPtr[] { ExtractedCroppedFT.GetDevice(Intent.Read) },
                                    //                        SizeRefine,
                                    //                        null,
                                    //                        new float[CurBatch * 2],
                                    //                        Helper.ToInterleaved(ParticleAnglesInFrames),
                                    //                        MagnificationCorrection * new float3(Species.HalfMap1Projector[GPUID].Oversampling,
                                    //                                                             Species.HalfMap1Projector[GPUID].Oversampling,
                                    //                                                             1),
                                    //                        new[] { Species.HalfMap1Projector[GPUID].t_DataRe, Species.HalfMap2Projector[GPUID].t_DataRe },
                                    //                        new[] { Species.HalfMap1Projector[GPUID].t_DataIm, Species.HalfMap2Projector[GPUID].t_DataIm },
                                    //                        Species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                    //                        new IntPtr((long)SpeciesParticleSubsets[Species] + batchStart * sizeof(int)),
                                    //                        CurBatch,
                                    //                        1);


                                    GPU.MultiParticleCorr2D(FSC.GetDeviceSlice(f * 3, Intent.ReadWrite),
                                                            new IntPtr((long)FSCPerParticle.GetDevice(Intent.ReadWrite) + (SpeciesOffset + batchStart) * FSCPerParticle.Dims.X * 3 * sizeof(float)),
                                                            PhaseResiduals.GetDevice(Intent.ReadWrite),
                                                            FSCLength,
                                                            new IntPtr[] { ExtractedCroppedFT.GetDevice(Intent.Read) },
                                                            ExtractedCTF.GetDevice(Intent.Read),
                                                            SizeRefine,
                                                            ScaleFactor,
                                                            null,
                                                            new float[CurBatch * 2],
                                                            Helper.ToInterleaved(ParticleAnglesInFrames),
                                                            MagnificationCorrection,
                                                            0,
                                                            new[] { Species.HalfMap1Projector[GPUID].t_DataRe, Species.HalfMap2Projector[GPUID].t_DataRe },
                                                            new[] { Species.HalfMap1Projector[GPUID].t_DataIm, Species.HalfMap2Projector[GPUID].t_DataIm },
                                                            Species.HalfMap1Projector[GPUID].Oversampling,
                                                            Species.HalfMap1Projector[GPUID].DimsOversampled.X,
                                                            new IntPtr((long)SpeciesParticleSubsets[Species] + batchStart * sizeof(int)),
                                                            CurBatch,
                                                            1);
                                }
                            }

                            BeamTiltCorrection.Dispose();

                            CoordsCTFCropped.Dispose();
                            CoordsCTF.Dispose();
                            Extracted.Dispose();
                            ExtractedFT.Dispose();
                            ExtractedCropped.Dispose();
                            ExtractedCroppedFT.Dispose();
                            ExtractedCTF.Dispose();

                            GPU.DestroyFFTPlan(PlanForwSuper);
                            GPU.DestroyFFTPlan(PlanBackSuper);
                            GPU.DestroyFFTPlan(PlanForw);
                        }
                    }

                    FSC.WriteMRC(System.IO.Path.Combine(workingDirectory, "..", $"{RootName}_tilt{tiltID:D3}_fsc.mrc"), true);
                    FSC.Dispose();

                    FSCPerParticle.WriteMRC(System.IO.Path.Combine(workingDirectory, "..", $"{RootName}_tilt{tiltID:D3}_fscparticles.mrc"), true);
                    FSCPerParticle.Dispose();

                    PhaseResiduals.WriteMRC(System.IO.Path.Combine(workingDirectory, "..", RootName + "_phaseresiduals.mrc"), true);
                    PhaseResiduals.Dispose();

                    TableOut.Save(System.IO.Path.Combine(workingDirectory, "..", $"{RootName}_tilt{tiltID:D3}_fscparticles.star"));
                }

                #endregion

                #region Tear down

                foreach (var pair in SpeciesParticleImages)
                {
                    foreach (var ptr in SpeciesParticleImages[pair.Key])
                        GPU.FreeHostPinned(ptr);
                }

                #endregion
            }
        }


        public override long MultiParticleRefinementCalculateHostMemory(ProcessingOptionsMPARefine optionsMPA,
                                                                        Species[] allSpecies,
                                                                        DataSource dataSource)
        {
            long Result = 0;

            string DataHash = GetDataHash();
            int GPUID = GPU.GetDevice();

            foreach (var species in allSpecies)
            {
                int NParticles = species.GetParticles(DataHash).Length;

                int Size = species.HalfMap1Projector[GPUID].Dims.X;
                int SizeFull = species.HalfMap1Reconstruction[GPUID].Dims.X;

                int[] RelevantSizes = GetRelevantImageSizes(SizeFull, (float)optionsMPA.BFactorWeightingThreshold).Select(v => Math.Min(Size, v)).ToArray();

                Result += Helper.ArrayOfFunction(t => (new int3(RelevantSizes[t]).Slice().ElementsFFT()) * 2 * (long)NParticles * sizeof(float), NTilts).Sum();
            }

            return Result;
        }

        #endregion

        #region Helper methods

        #region GetPosition methods

        public float3[] GetPositionInAllTilts(float3 coords)
        {
            float3[] PerTiltCoords = new float3[NTilts];
            for (int i = 0; i < NTilts; i++)
                PerTiltCoords[i] = coords;

            return GetPositionInAllTilts(PerTiltCoords);
        }

        public float3[] GetPositionInAllTilts(float3[] coords)
        {
            float3[] Result = new float3[coords.Length];

            float3 VolumeCenter = VolumeDimensionsPhysical / 2;
            float2 ImageCenter = ImageDimensionsPhysical / 2;

            float GridStep = 1f / (NTilts - 1);
            float DoseStep = 1f / (MaxDose - MinDose);
            float _MinDose = MinDose;

            float3[] GridCoords = new float3[coords.Length];
            //float3[] TemporalGridCoords = new float3[coords.Length];
            float4[] TemporalGridCoords4 = new float4[coords.Length];
            for (int i = 0; i < coords.Length; i++)
            {
                int t = i % NTilts;

                GridCoords[i] = new float3(coords[i].X / VolumeDimensionsPhysical.X, coords[i].Y / VolumeDimensionsPhysical.Y, t * GridStep);
                //TemporalGridCoords[i] = new float3(GridCoords[i].X, GridCoords[i].Y, (Dose[t] - _MinDose) * DoseStep);
                TemporalGridCoords4[i] = new float4(GridCoords[i].X, GridCoords[i].Y, coords[i].Z / VolumeDimensionsPhysical.Z, (Dose[t] - _MinDose) * DoseStep);
            }

            //float[] GridAngleXInterp = GridAngleX.GetInterpolatedNative(GridCoords);
            //float[] GridAngleYInterp = GridAngleY.GetInterpolatedNative(GridCoords);
            //float[] GridAngleZInterp = GridAngleZ.GetInterpolatedNative(GridCoords);

            float[] GridVolumeWarpXInterp = GridVolumeWarpX.GetInterpolated(TemporalGridCoords4);
            float[] GridVolumeWarpYInterp = GridVolumeWarpY.GetInterpolated(TemporalGridCoords4);
            float[] GridVolumeWarpZInterp = GridVolumeWarpZ.GetInterpolated(TemporalGridCoords4);

            float[] GridDefocusInterp = GridCTFDefocus.GetInterpolatedNative(GridCoords.Take(NTilts).ToArray());

            Matrix3[] TiltMatrices = Helper.ArrayOfFunction(t => Matrix3.Euler(0, Angles[t] * Helper.ToRad, -TiltAxisAngles[t] * Helper.ToRad), NTilts);
            Matrix3[] TiltMatricesFlipped = null;
            if (AreAnglesInverted)
                TiltMatricesFlipped = Helper.ArrayOfFunction(t => Matrix3.Euler(0, -Angles[t] * Helper.ToRad, -TiltAxisAngles[t] * Helper.ToRad), NTilts);

            float3[] TransformedCoords = new float3[coords.Length];

            for (int i = 0; i < coords.Length; i++)
            {
                int t = i % NTilts;
                float3 Centered = coords[i] - VolumeCenter;
                
                Matrix3 Rotation = TiltMatrices[t];

                float3 SampleWarping = new float3(GridVolumeWarpXInterp[i],
                                                  GridVolumeWarpYInterp[i],
                                                  GridVolumeWarpZInterp[i]);
                Centered += SampleWarping;

                float3 Transformed = (Rotation * Centered);

                Transformed.X += TiltAxisOffsetX[t];   // Tilt axis offset is in image space
                Transformed.Y += TiltAxisOffsetY[t];

                Transformed.X += ImageCenter.X;
                Transformed.Y += ImageCenter.Y;

                TransformedCoords[i] = new float3(Transformed.X / ImageDimensionsPhysical.X, Transformed.Y / ImageDimensionsPhysical.Y, t * GridStep);

                Result[i] = Transformed;

                // Do the same, but now with Z coordinate and tilt angle flipped
                if (AreAnglesInverted)
                {
                    Rotation = TiltMatricesFlipped[t];
                    Centered.Z *= -1;

                    Transformed = (Rotation * Centered);

                    Result[i].Z = Transformed.Z;
                }
            }

            float[] GridMovementXInterp = GridMovementX.GetInterpolatedNative(TransformedCoords);
            float[] GridMovementYInterp = GridMovementY.GetInterpolatedNative(TransformedCoords);

            for (int i = 0; i < coords.Length; i++)
            {
                int t = i % NTilts;

                // Additional stage shift determined for this tilt
                Result[i].X -= GridMovementXInterp[i];
                Result[i].Y -= GridMovementYInterp[i];

                // Coordinates are in Angstrom, can be converted directly in um
                Result[i].Z = GridDefocusInterp[t] + 1e-4f * Result[i].Z;

                Result[i] *= SizeRoundingFactors;
            }

            return Result;
        }

        // No support for AreTiltAnglesInverted because this method is only used to trim partially covered voxels
        public float3[] GetPositionInAllTiltsNoLocalWarp(float3[] coords)
        {
            float3[] Result = new float3[coords.Length * NTilts];

            float3 VolumeCenter = VolumeDimensionsPhysical / 2;
            float2 ImageCenter = ImageDimensionsPhysical / 2;

            float GridStep = 1f / (NTilts - 1);
            float DoseStep = 1f / (MaxDose - MinDose);
            float _MinDose = MinDose;

            float3[] GridCoords = new float3[NTilts];
            float3[] TemporalGridCoords = new float3[NTilts];
            float4[] TemporalGridCoords4 = new float4[NTilts];
            for (int t = 0; t < NTilts; t++)
            {
                GridCoords[t] = new float3(0.5f, 0.5f, t * GridStep);
                TemporalGridCoords[t] = new float3(0.5f, 0.5f, Dose[t] * GridStep);
                TemporalGridCoords4[t] = new float4(0.5f, 0.5f, 0.5f, (Dose[t] - _MinDose) * DoseStep);
            }

            float[] GridVolumeWarpXInterp = GridVolumeWarpX.GetInterpolated(TemporalGridCoords4);
            float[] GridVolumeWarpYInterp = GridVolumeWarpY.GetInterpolated(TemporalGridCoords4);
            float[] GridVolumeWarpZInterp = GridVolumeWarpZ.GetInterpolated(TemporalGridCoords4);
            float3[] SampleWarpings = Helper.ArrayOfFunction(t => new float3(GridVolumeWarpXInterp[t],
                                                                             GridVolumeWarpYInterp[t],
                                                                             GridVolumeWarpZInterp[t]), NTilts);

            float[] GridMovementXInterp = GridMovementX.GetInterpolatedNative(GridCoords);
            float[] GridMovementYInterp = GridMovementY.GetInterpolatedNative(GridCoords);

            float[] GridDefocusInterp = Helper.ArrayOfFunction(t => GetTiltDefocus(t), NTilts);

            Matrix3[] TiltMatrices = Helper.ArrayOfFunction(t => Matrix3.Euler(0, Angles[t] * Helper.ToRad, -TiltAxisAngles[t] * Helper.ToRad), NTilts);

            Matrix3[] OverallRotations = Helper.ArrayOfFunction(t => TiltMatrices[t], NTilts);
            float3[] OverallOffsets = Helper.ArrayOfFunction(t => new float3(TiltAxisOffsetX[t] + ImageCenter.X - GridMovementXInterp[t],
                                                                             TiltAxisOffsetY[t] + ImageCenter.Y - GridMovementYInterp[t],
                                                                             GridDefocusInterp[t] * 1e4f), NTilts);

            for (int i = 0; i < coords.Length; i++)
            {
                float3 Centered = coords[i] - VolumeCenter;

                for (int t = 0; t < NTilts; t++)
                {
                    float3 Transformed = OverallRotations[t] * (Centered + SampleWarpings[t]) + OverallOffsets[t];
                    Transformed.Z *= 1e-4f;

                    Result[i * NTilts + t] = Transformed * SizeRoundingFactors;
                }
            }

            return Result;
        }

        public float3[] GetPositionsInOneTilt(float3[] coords, int tiltID)
        {
            float3[] Result = new float3[coords.Length];

            float3 VolumeCenter = VolumeDimensionsPhysical / 2;
            float2 ImageCenter = ImageDimensionsPhysical / 2;

            float GridStep = 1f / (NTilts - 1);
            float DoseStep = 1f / (MaxDose - MinDose);
            float _MinDose = MinDose;

            Matrix3 TiltMatrix = Matrix3.Euler(0, Angles[tiltID] * Helper.ToRad, -TiltAxisAngles[tiltID] * Helper.ToRad);
            Matrix3 TiltMatrixFlipped = AreAnglesInverted ? Matrix3.Euler(0, -Angles[tiltID] * Helper.ToRad, -TiltAxisAngles[tiltID] * Helper.ToRad) : null;

            for (int p = 0; p < coords.Length; p++)
            {
                float3 GridCoords = new float3(coords[p].X / VolumeDimensionsPhysical.X, coords[p].Y / VolumeDimensionsPhysical.Y, tiltID * GridStep);
                float3 Centered = coords[p] - VolumeCenter;

                Matrix3 Rotation = TiltMatrix;

                float4 TemporalGridCoords4 = new float4(GridCoords.X, GridCoords.Y, coords[p].Z / VolumeDimensionsPhysical.Z, (Dose[tiltID] - _MinDose) * DoseStep);
                float3 SampleWarping = new float3(GridVolumeWarpX.GetInterpolated(TemporalGridCoords4),
                                                  GridVolumeWarpY.GetInterpolated(TemporalGridCoords4),
                                                  GridVolumeWarpZ.GetInterpolated(TemporalGridCoords4));
                Centered += SampleWarping;

                float3 Transformed = (Rotation * Centered);

                Transformed.X += TiltAxisOffsetX[tiltID];   // Tilt axis offset is in image space
                Transformed.Y += TiltAxisOffsetY[tiltID];

                Transformed.X += ImageCenter.X;
                Transformed.Y += ImageCenter.Y;

                float3 TransformedCoords = new float3(Transformed.X / ImageDimensionsPhysical.X, Transformed.Y / ImageDimensionsPhysical.Y, tiltID * GridStep);

                // Additional stage shift determined for this tilt
                Transformed.X -= GridMovementX.GetInterpolated(TransformedCoords);
                Transformed.Y -= GridMovementY.GetInterpolated(TransformedCoords);

                // Coordinates are in Angstrom, can be converted directly in um
                Transformed.Z = GridCTFDefocus.GetInterpolated(GridCoords) + 1e-4f * Transformed.Z;

                Result[p] = Transformed;

                // Do the same, but now with Z coordinate and tilt angle flipped
                if (AreAnglesInverted)
                {
                    Rotation = TiltMatrixFlipped;

                    Centered.Z *= -1;

                    Transformed = (Rotation * Centered);

                    // Coordinates are in Angstrom, can be converted directly in um
                    Result[p].Z = GridCTFDefocus.GetInterpolated(GridCoords) + 1e-4f * Transformed.Z;
                }

                Result[p] *= SizeRoundingFactors;
            }

            return Result;
        }

        #endregion

        #region GetAngle methods

        public float3[] GetAngleInAllTilts(float3 coords)
        {
            float3[] PerTiltCoords = new float3[NTilts];
            for (int i = 0; i < NTilts; i++)
                PerTiltCoords[i] = coords;

            return GetAngleInAllTilts(PerTiltCoords);
        }

        public float3[] GetAngleInAllTilts(float3[] coords)
        {
            float3[] Result = new float3[coords.Length];

            float GridStep = 1f / (NTilts - 1);

            float3[] GridCoords = new float3[coords.Length];
            float3[] TemporalGridCoords = new float3[coords.Length];
            for (int i = 0; i < coords.Length; i++)
            {
                int t = i % NTilts;
                GridCoords[i] = new float3(coords[i].X / VolumeDimensionsPhysical.X, coords[i].Y / VolumeDimensionsPhysical.X, t * GridStep);
            }

            float[] GridAngleXInterp = GridAngleX.GetInterpolatedNative(GridCoords);
            float[] GridAngleYInterp = GridAngleY.GetInterpolatedNative(GridCoords);
            float[] GridAngleZInterp = GridAngleZ.GetInterpolatedNative(GridCoords);

            Matrix3[] TiltMatrices = Helper.ArrayOfFunction(t => Matrix3.Euler(0, Angles[t] * Helper.ToRad, -TiltAxisAngles[t] * Helper.ToRad), NTilts);

            for (int i = 0; i < coords.Length; i++)
            {
                int t = i % NTilts;

                Matrix3 CorrectionMatrix = Matrix3.RotateZ(GridAngleZInterp[i] * Helper.ToRad) *
                                           Matrix3.RotateY(GridAngleYInterp[i] * Helper.ToRad) *
                                           Matrix3.RotateX(GridAngleXInterp[i] * Helper.ToRad);

                Matrix3 Rotation = CorrectionMatrix * TiltMatrices[t];

                Result[i] = Matrix3.EulerFromMatrix(Rotation);
            }

            return Result;
        }

        public Matrix3[] GetParticleRotationMatrixInAllTilts(float3[] coords, float3[] angle)
        {
            Matrix3[] Result = new Matrix3[coords.Length];

            float GridStep = 1f / (NTilts - 1);

            float3[] GridCoords = new float3[coords.Length];
            float3[] TemporalGridCoords = new float3[coords.Length];
            for (int i = 0; i < coords.Length; i++)
            {
                int t = i % NTilts;
                GridCoords[i] = new float3(coords[i].X / VolumeDimensionsPhysical.X, coords[i].Y / VolumeDimensionsPhysical.X, t * GridStep);
            }

            float[] GridAngleXInterp = GridAngleX.GetInterpolatedNative(GridCoords);
            float[] GridAngleYInterp = GridAngleY.GetInterpolatedNative(GridCoords);
            float[] GridAngleZInterp = GridAngleZ.GetInterpolatedNative(GridCoords);

            Matrix3[] TiltMatrices = Helper.ArrayOfFunction(t => Matrix3.Euler(0, Angles[t] * Helper.ToRad, -TiltAxisAngles[t] * Helper.ToRad), NTilts);

            for (int i = 0; i < coords.Length; i++)
            {
                int t = i % NTilts;

                Matrix3 ParticleMatrix = Matrix3.Euler(angle[i].X * Helper.ToRad,
                                                       angle[i].Y * Helper.ToRad,
                                                       angle[i].Z * Helper.ToRad);


                Matrix3 CorrectionMatrix = Matrix3.RotateZ(GridAngleZInterp[i] * Helper.ToRad) *
                                           Matrix3.RotateY(GridAngleYInterp[i] * Helper.ToRad) *
                                           Matrix3.RotateX(GridAngleXInterp[i] * Helper.ToRad);

                Matrix3 Rotation = CorrectionMatrix * TiltMatrices[t] * ParticleMatrix;

                Result[i] = Rotation;
            }

            return Result;
        }

        public float3[] GetParticleAngleInAllTilts(float3 coords, float3 angle)
        {
            float3[] PerTiltCoords = new float3[NTilts];
            float3[] PerTiltAngles = new float3[NTilts];
            for (int i = 0; i < NTilts; i++)
            {
                PerTiltCoords[i] = coords;
                PerTiltAngles[i] = angle;
            }

            return GetParticleAngleInAllTilts(PerTiltCoords, PerTiltAngles);
        }

        public float3[] GetParticleAngleInAllTilts(float3[] coords, float3[] angle)
        {
            float3[] Result = new float3[coords.Length];

            Matrix3[] Matrices = GetParticleRotationMatrixInAllTilts(coords, angle);

            for (int i = 0; i < Result.Length; i++)
                Result[i] = Matrix3.EulerFromMatrix(Matrices[i]);

            return Result;
        }

        public float3[] GetAnglesInOneTilt(float3[] coords, float3[] particleAngles, int tiltID)
        {
            int NParticles = coords.Length;
            float3[] Result = new float3[NParticles];

            float GridStep = 1f / (NTilts - 1);

            for (int p = 0; p < NParticles; p++)
            {
                float3 GridCoords = new float3(coords[p].X / VolumeDimensionsPhysical.X, coords[p].Y / VolumeDimensionsPhysical.Y, tiltID * GridStep);

                Matrix3 ParticleMatrix = Matrix3.Euler(particleAngles[p].X * Helper.ToRad,
                                                       particleAngles[p].Y * Helper.ToRad,
                                                       particleAngles[p].Z * Helper.ToRad);

                Matrix3 TiltMatrix = Matrix3.Euler(0, Angles[tiltID] * Helper.ToRad, -TiltAxisAngles[tiltID] * Helper.ToRad);

                Matrix3 CorrectionMatrix = Matrix3.RotateZ(GridAngleZ.GetInterpolated(GridCoords) * Helper.ToRad) *
                                           Matrix3.RotateY(GridAngleY.GetInterpolated(GridCoords) * Helper.ToRad) *
                                           Matrix3.RotateX(GridAngleX.GetInterpolated(GridCoords) * Helper.ToRad);

                Matrix3 Rotation = CorrectionMatrix * TiltMatrix * ParticleMatrix;

                Result[p] = Matrix3.EulerFromMatrix(Rotation);
            }

            return Result;
        }

        #endregion

        #region GetImages methods

        public override Image GetImagesForOneParticle(ProcessingOptionsBase options, Image[] tiltData, int size, float3 coords, int planForw = 0, int maskDiameter = -1, int maskEdge = 8, Image result = null, Image resultFT = null)
        {
            float3[] PerTiltCoords = new float3[NTilts];
            for (int i = 0; i < NTilts; i++)
                PerTiltCoords[i] = coords;

            return GetImagesForOneParticle(options, tiltData, size, PerTiltCoords, planForw, maskDiameter, maskEdge, true, result, resultFT);
        }

        public override Image GetImagesForOneParticle(ProcessingOptionsBase options, Image[] tiltData, int size, float3[] coordsMoving, int planForw = 0, int maskDiameter = -1, int maskEdge = 8, bool doDecenter = true, Image result = null, Image resultFT = null)
        {
            float3[] ImagePositions = GetPositionInAllTilts(coordsMoving);
            for (int t = 0; t < NTilts; t++)
                ImagePositions[t] /= (float)options.BinnedPixelSizeMean;

            Image Result = result == null ? new Image(new int3(size, size, NTilts)) : result;
            //float[][] ResultData = Result.GetHost(Intent.Write);
            float3[] Shifts = new float3[NTilts];

            int Decenter = doDecenter ? size / 2 : 0;

            IntPtr[] TiltSources = new IntPtr[NTilts];
            int3[] h_Origins = new int3[NTilts];

            for (int t = 0; t < NTilts; t++)
            {
                int3 DimsMovie = tiltData[t].Dims;

                ImagePositions[t] -= size / 2;

                int2 IntPosition = new int2((int)ImagePositions[t].X, (int)ImagePositions[t].Y);
                float2 Residual = new float2(-(ImagePositions[t].X - IntPosition.X), -(ImagePositions[t].Y - IntPosition.Y));
                IntPosition.X = (IntPosition.X + DimsMovie.X * 99) % DimsMovie.X;                                               // In case it is negative, for the periodic boundaries modulo later
                IntPosition.Y = (IntPosition.Y + DimsMovie.Y * 99) % DimsMovie.Y;
                Shifts[t] = new float3(Residual.X + Decenter, Residual.Y + Decenter, 0);                                        // Include an fftshift() for Fourier-space rotations later

                TiltSources[t] = tiltData[t].GetDevice(Intent.Read);
                h_Origins[t] = new int3(IntPosition.X, IntPosition.Y, 0);
            }

            GPU.ExtractMultisource(TiltSources,
                                   Result.GetDevice(Intent.Write),
                                   tiltData[0].Dims,
                                   new int3(size).Slice(),
                                   Helper.ToInterleaved(h_Origins),
                                   NTilts,
                                   (uint)NTilts);

            //GPU.NormParticles(Result.GetDevice(Intent.Read),
            //                  Result.GetDevice(Intent.Write),
            //                  Result.Dims.Slice(),
            //                  (uint)Result.Dims.X / 3,
            //                  false,
            //                  (uint)Result.Dims.Z);

            if (maskDiameter > 0)
                GPU.SphereMask(Result.GetDevice(Intent.Read),
                                Result.GetDevice(Intent.Write),
                                Result.Dims.Slice(),
                                maskDiameter / 2f,
                                maskEdge,
                                false,
                                (uint)Result.Dims.Z);

            Image ResultFT = resultFT == null ? new Image(IntPtr.Zero, new int3(size, size, NTilts), true, true) : resultFT;
            GPU.FFT(Result.GetDevice(Intent.Read),
                    ResultFT.GetDevice(Intent.Write),
                    Result.Dims,
                    (uint)Result.Dims.Z,
                    planForw);
            ResultFT.Multiply(1f / (size * size));
            ResultFT.ShiftSlices(Shifts);

            if (result == null)
                Result.Dispose();

            return ResultFT;
        }

        #endregion

        #region GetCTFs methods

        public override Image GetCTFsForOneParticle(ProcessingOptionsBase options, float3 coords, Image ctfCoords, Image gammaCorrection, bool weighted = true, bool weightsonly = false, bool useglobalweights = false, Image result = null)
        {
            float3[] PerTiltCoords = new float3[NTilts];
            for (int i = 0; i < NTilts; i++)
                PerTiltCoords[i] = coords;

            return GetCTFsForOneParticle(options, PerTiltCoords, ctfCoords, gammaCorrection, weighted, weightsonly, useglobalweights, result);
        }

        public override Image GetCTFsForOneParticle(ProcessingOptionsBase options, float3[] coordsMoving, Image ctfCoords, Image gammaCorrection, bool weighted = true, bool weightsonly = false, bool useglobalweights = false, Image result = null)
        {
            float3[] ImagePositions = GetPositionInAllTilts(coordsMoving);

            float GridStep = 1f / (NTilts - 1);
            CTFStruct[] Params = new CTFStruct[NTilts];
            for (int t = 0; t < NTilts; t++)
            {
                decimal Defocus = (decimal)ImagePositions[t].Z;
                decimal DefocusDelta = (decimal)GetTiltDefocusDelta(t);
                decimal DefocusAngle = (decimal)GetTiltDefocusAngle(t);

                CTF CurrCTF = CTF.GetCopy();
                CurrCTF.PixelSize = options.BinnedPixelSizeMean;
                if (!weightsonly)
                {
                    CurrCTF.Defocus = Defocus;
                    CurrCTF.DefocusDelta = DefocusDelta;
                    CurrCTF.DefocusAngle = DefocusAngle;
                }
                else
                {
                    CurrCTF.Defocus = 0;
                    CurrCTF.DefocusDelta = 0;
                    CurrCTF.Cs = 0;
                    CurrCTF.Amplitude = 1;
                }

                if (weighted)
                {
                    float3 InterpAt = new float3(coordsMoving[t].X / VolumeDimensionsPhysical.X,
                                                 coordsMoving[t].Y / VolumeDimensionsPhysical.Y,
                                                 t * GridStep);

                    if (GridDoseWeights.Dimensions.Elements() <= 1)
                        CurrCTF.Scale = (decimal)Math.Cos(Angles[t] * Helper.ToRad);
                    else
                        CurrCTF.Scale = (decimal)GridDoseWeights.GetInterpolated(new float3(0.5f, 0.5f, t * GridStep)) *
                                        (decimal)GridLocationWeights.GetInterpolated(InterpAt);

                    CurrCTF.Scale *= UseTilt[t] ? 1 : 0.0001M;

                    if (GridDoseBfacs.Dimensions.Elements() <= 1)
                        CurrCTF.Bfactor = (decimal)-Dose[t] * 4;
                    else
                        CurrCTF.Bfactor = (decimal)Math.Min(GridDoseBfacs.GetInterpolated(new float3(0.5f, 0.5f, t * GridStep)), -Dose[t] * 3) +
                                          (decimal)GridLocationBfacs.GetInterpolated(InterpAt);

                    CurrCTF.BfactorDelta = (decimal)GridDoseBfacsDelta.GetInterpolated(new float3(0.5f, 0.5f, t * GridStep));
                    CurrCTF.BfactorAngle = (decimal)GridDoseBfacsAngle.GetInterpolated(new float3(0.5f, 0.5f, t * GridStep));

                    if (useglobalweights)
                    {
                        CurrCTF.Bfactor += (decimal)GlobalBfactor;
                        CurrCTF.Scale *= (decimal)GlobalWeight;
                    }
                }

                Params[t] = CurrCTF.ToStruct();
            }

            Image Result = result == null ? new Image(IntPtr.Zero, new int3(ctfCoords.Dims.X, ctfCoords.Dims.Y, NTilts), true) : result;
            GPU.CreateCTF(Result.GetDevice(Intent.Write),
                                           ctfCoords.GetDevice(Intent.Read),
                                           gammaCorrection == null ? IntPtr.Zero : gammaCorrection.GetDevice(Intent.Read),
                                           (uint)ctfCoords.ElementsSliceComplex,
                                           Params,
                                           false,
                                           (uint)NTilts);

            return Result;
        }

        public void GetCTFsForOneTilt(float pixelSize, float[] defoci, float3[] coords, Image ctfCoords, Image gammaCorrection, int tiltID, Image outSimulated, bool weighted = true, bool weightsonly = false, bool useglobalweights = false)
        {
            int NParticles = defoci.Length;
            CTFStruct[] Params = new CTFStruct[NParticles];

            float GridStep = 1f / (NTilts - 1);

            decimal DefocusDelta = (decimal)GetTiltDefocusDelta(tiltID);
            decimal DefocusAngle = (decimal)GetTiltDefocusAngle(tiltID);

            CTF ProtoCTF = CTF.GetCopy();
            ProtoCTF.PixelSize = (decimal)pixelSize;
            if (!weightsonly)
            {
                ProtoCTF.DefocusDelta = DefocusDelta;
                ProtoCTF.DefocusAngle = DefocusAngle;
            }
            else
            {
                ProtoCTF.Defocus = 0;
                ProtoCTF.DefocusDelta = 0;
                ProtoCTF.Cs = 0;
                ProtoCTF.Amplitude = 1;
            }

            decimal Bfac = 0;
            decimal BfacDelta = 0;
            decimal BfacAngle = 0;
            decimal Weight = 1;

            if (weighted)
            {
                if (GridDoseBfacs.Dimensions.Elements() <= 1)
                    Bfac = (decimal)-Dose[tiltID] * 4;
                else
                    Bfac = (decimal)Math.Min(GridDoseBfacs.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep)), -Dose[tiltID] * 3);

                if (GridDoseWeights.Dimensions.Elements() <= 1)
                    Weight = (decimal)Math.Cos(Angles[tiltID] * Helper.ToRad);
                else
                    Weight = (decimal)GridDoseWeights.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));

                Weight *= UseTilt[tiltID] ? 1 : 0.0001M;

                if (useglobalweights)
                {
                    Bfac += (decimal)GlobalBfactor;
                    Weight *= (decimal)GlobalWeight;
                }

                BfacDelta = (decimal)GridDoseBfacsDelta.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));
                BfacAngle = (decimal)GridDoseBfacsAngle.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));
            }

            for (int p = 0; p < NParticles; p++)
            {
                if (weighted)
                {
                    ProtoCTF.Bfactor = Bfac;
                    ProtoCTF.BfactorDelta = BfacDelta;
                    ProtoCTF.BfactorAngle = BfacAngle;
                    ProtoCTF.Scale = Weight;

                    float3 InterpAt = new float3(coords[p].X / VolumeDimensionsPhysical.X,
                                                 coords[p].Y / VolumeDimensionsPhysical.Y,
                                                 0.5f);
                    ProtoCTF.Bfactor += (decimal)GridLocationBfacs.GetInterpolated(InterpAt);
                    ProtoCTF.Scale *= (decimal)GridLocationWeights.GetInterpolated(InterpAt);
                }

                if (!weightsonly)
                    ProtoCTF.Defocus = (decimal)defoci[p];

                Params[p] = ProtoCTF.ToStruct();
            }

            GPU.CreateCTF(outSimulated.GetDevice(Intent.Write),
                                                 ctfCoords.GetDevice(Intent.Read),
                                                 gammaCorrection == null ? IntPtr.Zero : gammaCorrection.GetDevice(Intent.Read),
                                                 (uint)ctfCoords.ElementsSliceComplex,
                                                 Params,
                                                 false,
                                                 (uint)NParticles);
        }

        public void GetComplexCTFsForOneTilt(float pixelSize, float[] defoci, float3[] coords, Image ctfCoords, Image gammaCorrection, int tiltID, bool reverse, Image outSimulated, bool weighted = true, bool weightsonly = false, bool useglobalweights = false)
        {
            int NParticles = defoci.Length;
            CTFStruct[] Params = new CTFStruct[NParticles];

            float GridStep = 1f / (NTilts - 1);

            decimal DefocusDelta = (decimal)GetTiltDefocusDelta(tiltID);
            decimal DefocusAngle = (decimal)GetTiltDefocusAngle(tiltID);

            CTF ProtoCTF = CTF.GetCopy();
            ProtoCTF.PixelSize = (decimal)pixelSize;
            if (!weightsonly)
            {
                ProtoCTF.DefocusDelta = DefocusDelta;
                ProtoCTF.DefocusAngle = DefocusAngle;
            }
            else
            {
                ProtoCTF.Defocus = 0;
                ProtoCTF.DefocusDelta = 0;
                ProtoCTF.Cs = 0;
                ProtoCTF.Amplitude = 1;
            }

            decimal Bfac = 0;
            decimal BfacDelta = 0;
            decimal BfacAngle = 0;
            decimal Weight = 1;

            if (weighted)
            {
                if (GridDoseBfacs.Dimensions.Elements() <= 1)
                    Bfac = (decimal)-Dose[tiltID] * 4;
                else
                    Bfac = (decimal)Math.Min(GridDoseBfacs.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep)), -Dose[tiltID] * 3);

                if (GridDoseWeights.Dimensions.Elements() <= 1)
                    Weight = (decimal)Math.Cos(Angles[tiltID] * Helper.ToRad);
                else
                    Weight = (decimal)GridDoseWeights.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));

                Weight *= UseTilt[tiltID] ? 1 : 0.0001M;

                if (useglobalweights)
                {
                    Bfac += (decimal)GlobalBfactor;
                    Weight *= (decimal)GlobalWeight;
                }

                BfacDelta = (decimal)GridDoseBfacsDelta.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));
                BfacAngle = (decimal)GridDoseBfacsAngle.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));
            }

            for (int p = 0; p < NParticles; p++)
            {
                if (weighted)
                {
                    ProtoCTF.Bfactor = Bfac;
                    ProtoCTF.BfactorDelta = BfacDelta;
                    ProtoCTF.BfactorAngle = BfacAngle;
                    ProtoCTF.Scale = Weight;

                    float3 InterpAt = new float3(coords[p].X / VolumeDimensionsPhysical.X,
                                                 coords[p].Y / VolumeDimensionsPhysical.Y,
                                                 0.5f);
                    ProtoCTF.Bfactor += (decimal)GridLocationBfacs.GetInterpolated(InterpAt);
                    ProtoCTF.Scale *= (decimal)GridLocationWeights.GetInterpolated(InterpAt);
                }

                if (!weightsonly)
                    ProtoCTF.Defocus = (decimal)defoci[p];

                Params[p] = ProtoCTF.ToStruct();
            }

            GPU.CreateCTFComplex(outSimulated.GetDevice(Intent.Write),
                                                 ctfCoords.GetDevice(Intent.Read),
                                                 gammaCorrection == null ? IntPtr.Zero : gammaCorrection.GetDevice(Intent.Read),
                                                 (uint)ctfCoords.ElementsSliceComplex,
                                                 Params,
                                                 reverse,
                                                 (uint)NParticles);
        }

        public CTF[] GetCTFParamsForOneTilt(float pixelSize, float[] defoci, float3[] coords, int tiltID, bool weighted = true, bool weightsonly = false, bool useglobalweights = false)
        {
            int NParticles = defoci.Length;
            CTF[] Params = new CTF[NParticles];

            float GridStep = 1f / (NTilts - 1);

            decimal DefocusDelta = (decimal)GetTiltDefocusDelta(tiltID);
            decimal DefocusAngle = (decimal)GetTiltDefocusAngle(tiltID);

            CTF ProtoCTF = CTF.GetCopy();
            ProtoCTF.PixelSize = (decimal)pixelSize;
            if (!weightsonly)
            {
                ProtoCTF.DefocusDelta = DefocusDelta;
                ProtoCTF.DefocusAngle = DefocusAngle;
            }
            else
            {
                ProtoCTF.Defocus = 0;
                ProtoCTF.DefocusDelta = 0;
                ProtoCTF.Cs = 0;
                ProtoCTF.Amplitude = 1;
            }

            decimal Bfac = 0;
            decimal BfacDelta = 0;
            decimal BfacAngle = 0;
            decimal Weight = 1;

            if (weighted)
            {
                if (GridDoseBfacs.Dimensions.Elements() <= 1)
                    Bfac = (decimal)-Dose[tiltID] * 4;
                else
                    Bfac = (decimal)Math.Min(GridDoseBfacs.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep)), -Dose[tiltID] * 3);

                if (GridDoseWeights.Dimensions.Elements() <= 1)
                    Weight = (decimal)Math.Cos(Angles[tiltID] * Helper.ToRad);
                else
                    Weight = (decimal)GridDoseWeights.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));

                Weight *= UseTilt[tiltID] ? 1 : 0.0001M;

                if (useglobalweights)
                {
                    Bfac += (decimal)GlobalBfactor;
                    Weight *= (decimal)GlobalWeight;
                }

                BfacDelta = (decimal)GridDoseBfacsDelta.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));
                BfacAngle = (decimal)GridDoseBfacsAngle.GetInterpolated(new float3(0.5f, 0.5f, tiltID * GridStep));
            }

            for (int p = 0; p < NParticles; p++)
            {
                if (weighted)
                {
                    ProtoCTF.Bfactor = Bfac;
                    ProtoCTF.BfactorDelta = BfacDelta;
                    ProtoCTF.BfactorAngle = BfacAngle;
                    ProtoCTF.Scale = Weight;

                    float3 InterpAt = new float3(coords[p].X / VolumeDimensionsPhysical.X,
                                                 coords[p].Y / VolumeDimensionsPhysical.Y,
                                                 0.5f);
                    ProtoCTF.Bfactor += (decimal)GridLocationBfacs.GetInterpolated(InterpAt);
                    ProtoCTF.Scale *= (decimal)GridLocationWeights.GetInterpolated(InterpAt);
                }

                if (!weightsonly)
                    ProtoCTF.Defocus = (decimal)defoci[p];

                Params[p] = ProtoCTF.GetCopy();
            }

            return Params;
        }

        #endregion

        #region Many-particles GetImages and GetCTFs

        public Image GetParticleImagesFromOneTilt(Image tiltStack, int size, float3[] particleOrigins, int angleID, bool normalize)
        {
            int NParticles = particleOrigins.Length;

            float3[] ImagePositions = GetPositionsInOneTilt(particleOrigins, angleID);

            Image Result = new Image(new int3(size, size, NParticles));
            float[][] ResultData = Result.GetHost(Intent.Write);
            float3[] Shifts = new float3[NParticles];

            int3 DimsStack = tiltStack.Dims;

            Parallel.For(0, NParticles, p =>
            {
                ImagePositions[p] -= new float3(size / 2, size / 2, 0);
                int2 IntPosition = new int2((int)ImagePositions[p].X, (int)ImagePositions[p].Y);
                float2 Residual = new float2(-(ImagePositions[p].X - IntPosition.X), -(ImagePositions[p].Y - IntPosition.Y));
                Residual -= size / 2;
                Shifts[p] = new float3(Residual);

                float[] OriginalData;
                lock (tiltStack)
                    OriginalData = tiltStack.GetHost(Intent.Read)[angleID];

                float[] ImageData = ResultData[p];
                for (int y = 0; y < size; y++)
                {
                    int PosY = (y + IntPosition.Y + DimsStack.Y) % DimsStack.Y;
                    for (int x = 0; x < size; x++)
                    {
                        int PosX = (x + IntPosition.X + DimsStack.X) % DimsStack.X;
                        ImageData[y * size + x] = OriginalData[PosY * DimsStack.X + PosX];
                    }
                }
            });
            if (normalize)
                GPU.NormParticles(Result.GetDevice(Intent.Read),
                                  Result.GetDevice(Intent.Write),
                                  Result.Dims.Slice(),
                                  (uint)(123 / CTF.PixelSize),     // FIX THE PARTICLE RADIUS!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
                                  true,
                                  (uint)NParticles);
            //Result.WriteMRC($"d_paticleimages_{angleID:D3}.mrc");

            Result.ShiftSlices(Shifts);

            Image ResultFT = Result.AsFFT();
            Result.Dispose();

            return ResultFT;
        }

        public Image GetParticleSeriesFromMovies(Movie[] movies, Image[] movieData, int size, float3[] particleOrigins, float pixelSize, int planForw = 0)
        {
            int NParticles = particleOrigins.Length;
            Image Result = new Image(new int3(size, size, NParticles * NTilts), true, true);
            float[][] ResultData = Result.GetHost(Intent.Write);

            int PlanForw = planForw > 0 ? planForw : GPU.CreateFFTPlan(new int3(size, size, 1), (uint)NParticles);

            Image ParticleExtracts = new Image(IntPtr.Zero, new int3(size, size, NParticles));
            Image ParticleExtractsFT = new Image(IntPtr.Zero, new int3(size, size, NParticles), true, true);

            for (int t = 0; t < NTilts; t++)
            {
                float3 Scaling = new float3(1 / pixelSize, 1 / pixelSize, 1);
                float3 DimsInv = new float3(1f / movieData[t].Dims.X,
                                            1f / movieData[t].Dims.Y,
                                            1f / Math.Max(1, movieData[t].Dims.Z));

                float3[] ImagePositions = GetPositionsInOneTilt(particleOrigins, t);
                for (int p = 0; p < NParticles; p++)
                    ImagePositions[p] *= Scaling;       // Tilt image positions are returned in Angstroms initially

                float3[] MovieGridPositions = new float3[NParticles];
                for (int p = 0; p < NParticles; p++)
                    MovieGridPositions[p] = new float3(ImagePositions[p].X * DimsInv.X,
                                                       ImagePositions[p].Y * DimsInv.Y,
                                                       0);

                int3[] ExtractPositions = new int3[NParticles];
                float3[] ResidualShifts = new float3[NParticles];

                Image ParticleSumsFT = new Image(IntPtr.Zero, new int3(size, size, NParticles), true, true);
                ParticleSumsFT.Fill(0);

                for (int z = 0; z < movieData[t].Dims.Z; z++)
                {
                    for (int p = 0; p < NParticles; p++)
                        MovieGridPositions[p].Z = z * DimsInv.Z;

                    float2[] FrameShifts = movies[t].GetShiftFromPyramid(MovieGridPositions);
                    for (int p = 0; p < NParticles; p++)
                    {
                        float3 Shifted = new float3(ImagePositions[p].X - FrameShifts[p].X / pixelSize,     // Don't forget, shifts are stored in Angstroms
                                                    ImagePositions[p].Y - FrameShifts[p].Y / pixelSize,
                                                    0);
                        ExtractPositions[p] = new int3(Shifted);
                        ResidualShifts[p] = new float3(ExtractPositions[p].X - Shifted.X + size / 2,
                                                       ExtractPositions[p].Y - Shifted.Y + size / 2,
                                                       0);

                        GPU.Extract(movieData[t].GetDeviceSlice(z, Intent.Read),
                                    ParticleExtracts.GetDevice(Intent.Write),
                                    movieData[t].Dims.Slice(),
                                    new int3(size, size, 1),
                                    Helper.ToInterleaved(ExtractPositions),
                                    (uint)NParticles);

                        GPU.FFT(ParticleExtracts.GetDevice(Intent.Read),
                                ParticleExtractsFT.GetDevice(Intent.Write),
                                ParticleExtracts.Dims.Slice(),
                                (uint)NParticles,
                                PlanForw);

                        ParticleExtractsFT.ShiftSlices(ResidualShifts);

                        ParticleSumsFT.Add(ParticleExtracts);
                    }
                }

                ParticleSumsFT.Multiply(1f / size / size / movieData[t].Dims.Z);

                float[][] ParticleSumsFTData = ParticleSumsFT.GetHost(Intent.Read);
                for (int p = 0; p < NParticles; p++)
                    ResultData[p * NTilts + t] = ParticleSumsFTData[p];

                ParticleSumsFT.Dispose();
                movieData[t].FreeDevice();
            }

            ParticleExtracts.Dispose();
            ParticleExtractsFT.Dispose();
            if (planForw <= 0)
                GPU.DestroyFFTPlan(PlanForw);

            return Result;
        }

        #endregion

        public override int[] GetRelevantImageSizes(int fullSize, float weightingThreshold)
        {
            int[] Result = new int[NTilts];

            float[][] AllWeights = new float[NTilts][];

            float GridStep = 1f / (NTilts - 1);
            for (int t = 0; t < NTilts; t++)
            {
                CTF CurrCTF = CTF.GetCopy();

                CurrCTF.Defocus = 0;
                CurrCTF.DefocusDelta = 0;
                CurrCTF.Cs = 0;
                CurrCTF.Amplitude = 1;

                if (GridDoseBfacs.Dimensions.Elements() <= 1)
                    CurrCTF.Bfactor = (decimal)-Dose[t] * 4;
                else
                    CurrCTF.Bfactor = (decimal)(GridDoseBfacs.GetInterpolated(new float3(0.5f, 0.5f, t * GridStep)) +
                                                Math.Abs(GridDoseBfacsDelta.GetInterpolated(new float3(0.5f, 0.5f, t * GridStep))));

                AllWeights[t] = CurrCTF.Get1D(fullSize / 2, false);
            }

            int elementID = IndicesSortedDose[0];
            if (GridDoseBfacs.Dimensions.Elements() > 1)
                (elementID, _) = MathHelper.MaxElement(GridDoseBfacs.FlatValues);
            float[] LowerDoseWeights = AllWeights[elementID].ToList().ToArray();

            for (int t = 0; t < NTilts; t++)
            {
                for (int i = 0; i < LowerDoseWeights.Length; i++)
                    AllWeights[t][i] /= LowerDoseWeights[i];

                int MaxShell = 0;
                while (MaxShell < AllWeights[t].Length)
                {
                    if (AllWeights[t][MaxShell] < weightingThreshold)
                        break;
                    MaxShell++;
                }

                Result[t] = Math.Max(2, Math.Min(fullSize, MaxShell * 2));
            }

            return Result;
        }

        public void GetSubtomoForOneParticle(TomoProcessingOptionsBase options, Movie[] tiltMovies, Image[] tiltData, float3 coords, float3 angles, Image ctfCoords, out Image subtomo, out Image subtomoCTF, int planForw = 0, int planBack = 0, int planForwCTF = 0, int planForwImages = 0)
        {
            int Size = ctfCoords.Dims.X;
            float3[] ImageAngles = GetAngleInAllTilts(coords);

            Image ImagesFT = null;//GetSubtomoImages(tiltStack, Size * downsample, coords, true);
            Image ImagesFTCropped = ImagesFT.AsPadded(new int2(Size, Size));
            ImagesFT.Dispose();

            Image CTFs = GetCTFsForOneParticle(options, coords, ctfCoords, null, true, false, false);
            //Image CTFWeights = GetSubtomoCTFs(coords, ctfCoords, true, true);

            ImagesFTCropped.Multiply(CTFs);    // Weight and phase-flip image FTs by CTF, which still has its sign here
            //ImagesFT.Multiply(CTFWeights);
            CTFs.Abs();                 // CTF has to be positive from here on since image FT phases are now flipped

            // CTF has to be converted to complex numbers with imag = 0, and weighted by itself
            float2[] CTFsComplexData = new float2[CTFs.ElementsComplex];
            float[] CTFsContinuousData = CTFs.GetHostContinuousCopy();
            for (int i = 0; i < CTFsComplexData.Length; i++)
                CTFsComplexData[i] = new float2(CTFsContinuousData[i] * CTFsContinuousData[i], 0);

            Image CTFsComplex = new Image(CTFsComplexData, CTFs.Dims, true);

            Projector ProjSubtomo = new Projector(new int3(Size, Size, Size), 2);
            lock (GPU.Sync)
                ProjSubtomo.BackProject(ImagesFTCropped, CTFs, ImageAngles, MagnificationCorrection);
            subtomo = ProjSubtomo.Reconstruct(false, "C1", planForw, planBack, planForwCTF);
            ProjSubtomo.Dispose();

            GPU.NormParticles(subtomo.GetDevice(Intent.Read),
                              subtomo.GetDevice(Intent.Write),
                              subtomo.Dims,
                              (uint)(123 / CTF.PixelSize),     // FIX THE PARTICLE RADIUS!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
                              false,
                              1);
            //subtomo = new Image(new int3(1, 1, 1));

            Projector ProjCTF = new Projector(new int3(Size, Size, Size), 2);
            lock (GPU.Sync)
                ProjCTF.BackProject(CTFsComplex, CTFs, ImageAngles, MagnificationCorrection);
            subtomoCTF = ProjCTF.Reconstruct(true, "C1", planForw, planBack, planForwCTF);
            ProjCTF.Dispose();
            //subtomoCTF = new Image(new int3(1, 1, 1));

            ImagesFTCropped.Dispose();
            CTFs.Dispose();
            //CTFWeights.Dispose();
            CTFsComplex.Dispose();
        }


        static Image[] RawDataBuffers = new Image[GPU.GetDeviceCount()];
        static Image[][] ScaledTiltBuffers = new Image[GPU.GetDeviceCount()][];
        public void LoadMovieData(ProcessingOptionsBase options, bool doFlatten, out Movie[] movies, out Image[] movieData)
        {
            if (TiltMoviePaths.Length != NTilts)
                throw new Exception("A valid path is needed for each tilt.");

            if (options.EERGroupFrames > 0)
                HeaderEER.GroupNFrames = options.EERGroupFrames;

            movies = new Movie[NTilts];

            for (int t = 0; t < NTilts; t++)
                movies[t] = new Movie(DirectoryName + TiltMoviePaths[t]);

            MapHeader Header = MapHeader.ReadFromFile(DirectoryName + TiltMoviePaths[0]);

            ImageDimensionsPhysical = new float2(Header.Dimensions.X, Header.Dimensions.Y) * (float)options.PixelSizeMean;

            int2 DimsScaled = new int2((int)Math.Round(Header.Dimensions.X / (float)options.DownsampleFactor / 2) * 2,
                                        (int)Math.Round(Header.Dimensions.Y / (float)options.DownsampleFactor / 2) * 2);

            SizeRoundingFactors = new float3(DimsScaled.X / (Header.Dimensions.X / (float)options.DownsampleFactor),
                                             DimsScaled.Y / (Header.Dimensions.Y / (float)options.DownsampleFactor),
                                             1);

            Header = MapHeader.ReadFromFile(movies[0].AveragePath);
            if (Header.Dimensions.Z > 1)
                throw new Exception("This average has more than one layer.");

            bool DoScale = DimsScaled != new int2(Header.Dimensions);

            int PlanForw = 0, PlanBack = 0;
            if (DoScale)
            {
                PlanForw = GPU.CreateFFTPlan(Header.Dimensions.Slice(), 1);
                PlanBack = GPU.CreateIFFTPlan(new int3(DimsScaled), 1);
            }

            int CurrentDevice = GPU.GetDevice();

            #region Make sure reusable buffers are there and have correct dimensions

            if (RawDataBuffers[CurrentDevice] == null || RawDataBuffers[CurrentDevice].ElementsReal != Header.Dimensions.Elements())
            {
                if (RawDataBuffers[CurrentDevice] != null)
                    RawDataBuffers[CurrentDevice].Dispose();

                RawDataBuffers[CurrentDevice] = new Image(Header.Dimensions);
            }

            if (ScaledTiltBuffers[CurrentDevice] == null ||
                ScaledTiltBuffers[CurrentDevice].Length < NTilts ||
                ScaledTiltBuffers[CurrentDevice][0].ElementsReal != DimsScaled.Elements())
            {
                if (ScaledTiltBuffers[CurrentDevice] != null)
                    foreach (var item in ScaledTiltBuffers[CurrentDevice])
                        item.Dispose();

                ScaledTiltBuffers[CurrentDevice] = Helper.ArrayOfFunction(i => new Image(new int3(DimsScaled)), NTilts);
            }

            #endregion

            for (int t = 0; t < NTilts; t++)
            {
                IOHelper.ReadMapFloatPatient(50, 500,
                                             movies[t].AveragePath,
                                             new int2(1),
                                             0,
                                             typeof(float),
                                             new[] { 0 },
                                             null,
                                             RawDataBuffers[CurrentDevice].GetHost(Intent.Write));

                if (DoScale)
                {
                    GPU.Scale(RawDataBuffers[CurrentDevice].GetDevice(Intent.Read),
                              ScaledTiltBuffers[CurrentDevice][t].GetDevice(Intent.Write),
                              Header.Dimensions,
                              new int3(DimsScaled),
                              1,
                              PlanForw,
                              PlanBack,
                              IntPtr.Zero,
                              IntPtr.Zero);

                    ScaledTiltBuffers[CurrentDevice][t].FreeDevice();
                }
                else
                {
                    Array.Copy(RawDataBuffers[CurrentDevice].GetHost(Intent.Read)[0], 0,
                               ScaledTiltBuffers[CurrentDevice][t].GetHost(Intent.Write)[0], 0,
                               (int)Header.Dimensions.Elements());
                }
            }

            if (DoScale)
            {
                GPU.DestroyFFTPlan(PlanForw);
                GPU.DestroyFFTPlan(PlanBack);
            }

            movieData = ScaledTiltBuffers[CurrentDevice];
        }

        public void LoadMovieSizes(ProcessingOptionsBase options)
        {
            if (TiltMoviePaths.Length != NTilts)
                throw new Exception("A valid path is needed for each tilt.");

            if (options.EERGroupFrames > 0)
                HeaderEER.GroupNFrames = options.EERGroupFrames;

            MapHeader Header = MapHeader.ReadFromFile(DirectoryName + TiltMoviePaths[0]);
            ImageDimensionsPhysical = new float2(Header.Dimensions.X, Header.Dimensions.Y) * (float)options.PixelSizeMean;
        }

        static Image[] RawMaskBuffers = new Image[GPU.GetDeviceCount()];
        static Image[][] ScaledMaskBuffers = new Image[GPU.GetDeviceCount()][];
        public void LoadMovieMasks(ProcessingOptionsBase options, out Image[] maskData)
        {
            Console.WriteLine($"-First Loaded MovieMasks : {DirectoryName}{TiltMoviePaths[0]}, ");
            Console.WriteLine($"-# LoadedMovieMasks : {TiltMoviePaths.Length}");
            MapHeader Header = MapHeader.ReadFromFile(DirectoryName + TiltMoviePaths[0]);

            int2 DimsScaled = new int2((int)Math.Round(Header.Dimensions.X / (float)options.DownsampleFactor / 2) * 2,
                                       (int)Math.Round(Header.Dimensions.Y / (float)options.DownsampleFactor / 2) * 2);

            int CurrentDevice = GPU.GetDevice();

            #region Make sure reusable buffers are there and have correct dimensions

            if (ScaledMaskBuffers[CurrentDevice] == null ||
                ScaledMaskBuffers[CurrentDevice].Length < NTilts ||
                ScaledMaskBuffers[CurrentDevice][0].ElementsReal != DimsScaled.Elements())
            {
                if (ScaledMaskBuffers[CurrentDevice] != null)
                    foreach (var item in ScaledMaskBuffers[CurrentDevice])
                        item.Dispose();

                ScaledMaskBuffers[CurrentDevice] = Helper.ArrayOfFunction(i => new Image(new int3(DimsScaled)), NTilts);
            }

            #endregion

            maskData = new Image[NTilts];

            for (int t = 0; t < NTilts; t++)
            {
                Movie M = new Movie(DirectoryName + TiltMoviePaths[t]);
                string MaskPath = M.MaskPath;

                if (File.Exists(MaskPath))
                {
                    MapHeader MaskHeader = MapHeader.ReadFromFile(MaskPath);

                    if (RawMaskBuffers[CurrentDevice] == null || RawMaskBuffers[CurrentDevice].ElementsReal != MaskHeader.Dimensions.Elements())
                    {
                        if (RawMaskBuffers[CurrentDevice] != null)
                            RawMaskBuffers[CurrentDevice].Dispose();

                        RawMaskBuffers[CurrentDevice] = new Image(MaskHeader.Dimensions);
                    }

                    // 50, 500.
                    TiffNative.ReadTIFFPatient(50, 500, MaskPath, 0, true, RawMaskBuffers[CurrentDevice].GetHost(Intent.Write)[0]);

                    #region Rescale

                    GPU.Scale(RawMaskBuffers[CurrentDevice].GetDevice(Intent.Read),
                              ScaledMaskBuffers[CurrentDevice][t].GetDevice(Intent.Write),
                              MaskHeader.Dimensions,
                              new int3(DimsScaled),
                              1,
                              0,
                              0,
                              IntPtr.Zero,
                              IntPtr.Zero);

                    ScaledMaskBuffers[CurrentDevice][t].Binarize(0.7f);
                    ScaledMaskBuffers[CurrentDevice][t].FreeDevice();

                    #endregion

                    maskData[t] = ScaledMaskBuffers[CurrentDevice][t];
                }
            }
        }

        static int[][] DirtErasureLabelsBuffer = new int[GPU.GetDeviceCount()][];
        static Image[] DirtErasureMaskBuffer = new Image[GPU.GetDeviceCount()];
        public void EraseDirt(Image tiltImage, Image tiltMask)
        {
            if (tiltMask == null)
                return;

            float[] ImageData = tiltImage.GetHost(Intent.ReadWrite)[0];

            int CurrentDevice = GPU.GetDevice();

            #region Make sure reusable buffers are there and correctly sized

            if (DirtErasureLabelsBuffer[CurrentDevice] == null || DirtErasureLabelsBuffer[CurrentDevice].Length != ImageData.Length)
                DirtErasureLabelsBuffer[CurrentDevice] = new int[ImageData.Length];

            if (DirtErasureMaskBuffer[CurrentDevice] == null || DirtErasureMaskBuffer[CurrentDevice].Dims != tiltMask.Dims)
            {
                if (DirtErasureMaskBuffer[CurrentDevice] != null)
                    DirtErasureMaskBuffer[CurrentDevice].Dispose();

                DirtErasureMaskBuffer[CurrentDevice] = new Image(tiltMask.Dims);
            }

            #endregion

            var Components = tiltMask.GetConnectedComponents(8, DirtErasureLabelsBuffer[CurrentDevice]);

            #region Inline Image.AsExpandedSmooth to use reusable buffer

            GPU.DistanceMapExact(tiltMask.GetDevice(Intent.Read), DirtErasureMaskBuffer[CurrentDevice].GetDevice(Intent.Write), tiltMask.Dims, 6);
            DirtErasureMaskBuffer[CurrentDevice].Multiply((float)Math.PI / 6f);
            DirtErasureMaskBuffer[CurrentDevice].Cos();
            DirtErasureMaskBuffer[CurrentDevice].Add(1);
            DirtErasureMaskBuffer[CurrentDevice].Multiply(0.5f);

            #endregion

            RandomNormal RandN = new RandomNormal();

            float[] MaskSmoothData = DirtErasureMaskBuffer[CurrentDevice].GetHost(Intent.Read)[0];
            DirtErasureMaskBuffer[CurrentDevice].FreeDevice();

            foreach (var component in Components)
            {
                if (component.NeighborhoodIndices.Length < 2)
                    continue;

                float[] NeighborhoodIntensities = Helper.IndexedSubset(ImageData, component.NeighborhoodIndices);
                float2 MeanStd = MathHelper.MeanAndStd(NeighborhoodIntensities);

                foreach (int id in component.ComponentIndices)
                    ImageData[id] = RandN.NextSingle(MeanStd.X, MeanStd.Y);

                foreach (int id in component.NeighborhoodIndices)
                    ImageData[id] = MathHelper.Lerp(ImageData[id], RandN.NextSingle(MeanStd.X, MeanStd.Y), MaskSmoothData[id]);
            }
        }

        public static void FreeDeviceBuffers()
        {
            foreach (var item in RawDataBuffers)
                item?.FreeDevice();
            foreach (var item in ScaledTiltBuffers)
                if (item != null)
                    foreach (var subitem in item)
                        subitem?.FreeDevice();

            foreach (var item in RawMaskBuffers)
                item?.FreeDevice();
            foreach (var item in ScaledMaskBuffers)
                if (item != null)
                    foreach (var subitem in item)
                        subitem?.FreeDevice();

            foreach (var item in DirtErasureMaskBuffer)
                item?.FreeDevice();
        }

        #endregion

        #region Load/save meta

        public override void LoadMeta()
        {
            if (!File.Exists(XMLPath))
                return;

            try
            {
                using (Stream SettingsStream = File.OpenRead(XMLPath))
                {
                    XPathDocument Doc = new XPathDocument(SettingsStream);
                    XPathNavigator Reader = Doc.CreateNavigator();
                    Reader.MoveToRoot();
                    Reader.MoveToFirstChild();

                    #region Attributes

                    AreAnglesInverted = XMLHelper.LoadAttribute(Reader, "AreAnglesInverted", AreAnglesInverted);
                    PlaneNormal = XMLHelper.LoadAttribute(Reader, "PlaneNormal", PlaneNormal);

                    GlobalBfactor = XMLHelper.LoadAttribute(Reader, "Bfactor", GlobalBfactor);
                    GlobalWeight = XMLHelper.LoadAttribute(Reader, "Weight", GlobalWeight);

                    MagnificationCorrection = XMLHelper.LoadAttribute(Reader, "MagnificationCorrection", MagnificationCorrection);

                    //_UnselectFilter = XMLHelper.LoadAttribute(Reader, "UnselectFilter", _UnselectFilter);
                    string UnselectManualString = XMLHelper.LoadAttribute(Reader, "UnselectManual", "null");
                    if (UnselectManualString != "null")
                        _UnselectManual = bool.Parse(UnselectManualString);
                    else
                        _UnselectManual = null;
                    CTFResolutionEstimate = XMLHelper.LoadAttribute(Reader, "CTFResolutionEstimate", CTFResolutionEstimate);

                    #endregion

                    #region Per-tilt propertries

                    {
                        XPathNavigator Nav = Reader.SelectSingleNode("//Angles");
                        if (Nav != null)
                            Angles = Nav.InnerXml.Split('\n').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    }

                    {
                        XPathNavigator Nav = Reader.SelectSingleNode("//Dose");
                        if (Nav != null)
                            Dose = Nav.InnerXml.Split('\n').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        else
                            Dose = new float[Angles.Length];
                    }

                    {
                        XPathNavigator Nav = Reader.SelectSingleNode("//UseTilt");
                        if (Nav != null)
                            UseTilt = Nav.InnerXml.Split('\n').Select(v => bool.Parse(v)).ToArray();
                        else
                            UseTilt = Helper.ArrayOfConstant(true, Angles.Length);
                    }

                    {
                        XPathNavigator Nav = Reader.SelectSingleNode("//AxisAngle");
                        if (Nav != null)
                            TiltAxisAngles = Nav.InnerXml.Split('\n').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        else
                            TiltAxisAngles = new float[Angles.Length];
                    }

                    {
                        XPathNavigator Nav = Reader.SelectSingleNode("//AxisOffsetX");
                        if (Nav != null)
                            TiltAxisOffsetX = Nav.InnerXml.Split('\n').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        else
                            TiltAxisOffsetX = new float[Angles.Length];
                    }

                    {
                        XPathNavigator Nav = Reader.SelectSingleNode("//AxisOffsetY");
                        if (Nav != null)
                            TiltAxisOffsetY = Nav.InnerXml.Split('\n').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        else
                            TiltAxisOffsetY = new float[Angles.Length];
                    }

                    {
                        XPathNavigator Nav = Reader.SelectSingleNode("//MoviePath");
                        if (Nav != null)
                            TiltMoviePaths = Nav.InnerXml.Split('\n').Select(v => v.Replace(" ", "").Replace("\r", "").Replace("\t", "")).ToArray();
                        else
                            TiltMoviePaths = new[] { "" };
                    }

                    #endregion

                    #region CTF fitting-related

                    {
                        TiltPS1D.Clear();
                        List<Tuple<int, float2[]>> TempPS1D = (from XPathNavigator NavPS1D in Reader.Select("//TiltPS1D")
                                                               let ID = int.Parse(NavPS1D.GetAttribute("ID", ""))
                                                               let NewPS1D = NavPS1D.InnerXml.Split(';').Select(v =>
                                                               {
                                                                   string[] Pair = v.Split('|');
                                                                   return new float2(float.Parse(Pair[0], CultureInfo.InvariantCulture), float.Parse(Pair[1], CultureInfo.InvariantCulture));
                                                               }).ToArray()
                                                               select new Tuple<int, float2[]>(ID, NewPS1D)).ToList();

                        TempPS1D.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                        foreach (var ps1d in TempPS1D)
                            TiltPS1D.Add(ps1d.Item2);
                    }

                    {
                        TiltSimulatedScale.Clear();
                        List<Tuple<int, Cubic1D>> TempScale = (from XPathNavigator NavSimScale in Reader.Select("//TiltSimulatedScale")
                                                               let ID = int.Parse(NavSimScale.GetAttribute("ID", ""))
                                                               let NewScale = new Cubic1D(NavSimScale.InnerXml.Split(';').Select(v =>
                                                               {
                                                                   string[] Pair = v.Split('|');
                                                                   return new float2(float.Parse(Pair[0], CultureInfo.InvariantCulture), float.Parse(Pair[1], CultureInfo.InvariantCulture));
                                                               }).ToArray())
                                                               select new Tuple<int, Cubic1D>(ID, NewScale)).ToList();

                        TempScale.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                        foreach (var scale in TempScale)
                            TiltSimulatedScale.Add(scale.Item2);
                    }

                    {
                        XPathNavigator NavPS1D = Reader.SelectSingleNode("//PS1D");
                        if (NavPS1D != null)
                            PS1D = NavPS1D.InnerXml.Split(';').Select(v =>
                            {
                                string[] Pair = v.Split('|');
                                return new float2(float.Parse(Pair[0], CultureInfo.InvariantCulture), float.Parse(Pair[1], CultureInfo.InvariantCulture));
                            }).ToArray();
                    }

                    {
                        XPathNavigator NavSimScale = Reader.SelectSingleNode("//SimulatedScale");
                        if (NavSimScale != null)
                            SimulatedScale = new Cubic1D(NavSimScale.InnerXml.Split(';').Select(v =>
                            {
                                string[] Pair = v.Split('|');
                                return new float2(float.Parse(Pair[0], CultureInfo.InvariantCulture), float.Parse(Pair[1], CultureInfo.InvariantCulture));
                            }).ToArray());
                    }

                    XPathNavigator NavCTF = Reader.SelectSingleNode("//CTF");
                    if (NavCTF != null)
                        CTF.ReadFromXML(NavCTF);

                    XPathNavigator NavOptionsCTF = Reader.SelectSingleNode("//OptionsCTF");
                    if (NavOptionsCTF != null)
                    {
                        ProcessingOptionsMovieCTF Temp = new ProcessingOptionsMovieCTF();
                        Temp.ReadFromXML(NavOptionsCTF);
                        OptionsCTF = Temp;
                    }

                    #endregion

                    #region Grids

                    XPathNavigator NavGridCTF = Reader.SelectSingleNode("//GridCTF");
                    if (NavGridCTF != null)
                        GridCTFDefocus = CubicGrid.Load(NavGridCTF);

                    XPathNavigator NavGridCTFDefocusDelta = Reader.SelectSingleNode("//GridCTFDefocusDelta");
                    if (NavGridCTFDefocusDelta != null)
                        GridCTFDefocusDelta = CubicGrid.Load(NavGridCTFDefocusDelta);

                    XPathNavigator NavGridCTFDefocusAngle = Reader.SelectSingleNode("//GridCTFDefocusAngle");
                    if (NavGridCTFDefocusAngle != null)
                        GridCTFDefocusAngle = CubicGrid.Load(NavGridCTFDefocusAngle);

                    XPathNavigator NavGridCTFPhase = Reader.SelectSingleNode("//GridCTFPhase");
                    if (NavGridCTFPhase != null)
                        GridCTFPhase = CubicGrid.Load(NavGridCTFPhase);

                    XPathNavigator NavMoveX = Reader.SelectSingleNode("//GridMovementX");
                    if (NavMoveX != null)
                        GridMovementX = CubicGrid.Load(NavMoveX);

                    XPathNavigator NavMoveY = Reader.SelectSingleNode("//GridMovementY");
                    if (NavMoveY != null)
                        GridMovementY = CubicGrid.Load(NavMoveY);

                    XPathNavigator NavVolumeWarpX = Reader.SelectSingleNode("//GridVolumeWarpX");
                    if (NavVolumeWarpX != null)
                        GridVolumeWarpX = LinearGrid4D.Load(NavVolumeWarpX);

                    XPathNavigator NavVolumeWarpY = Reader.SelectSingleNode("//GridVolumeWarpY");
                    if (NavVolumeWarpY != null)
                        GridVolumeWarpY = LinearGrid4D.Load(NavVolumeWarpY);

                    XPathNavigator NavVolumeWarpZ = Reader.SelectSingleNode("//GridVolumeWarpZ");
                    if (NavVolumeWarpZ != null)
                        GridVolumeWarpZ = LinearGrid4D.Load(NavVolumeWarpZ);

                    XPathNavigator NavAngleX = Reader.SelectSingleNode("//GridAngleX");
                    if (NavAngleX != null)
                        GridAngleX = CubicGrid.Load(NavAngleX);

                    XPathNavigator NavAngleY = Reader.SelectSingleNode("//GridAngleY");
                    if (NavAngleY != null)
                        GridAngleY = CubicGrid.Load(NavAngleY);

                    XPathNavigator NavAngleZ = Reader.SelectSingleNode("//GridAngleZ");
                    if (NavAngleZ != null)
                        GridAngleZ = CubicGrid.Load(NavAngleZ);

                    XPathNavigator NavDoseBfacs = Reader.SelectSingleNode("//GridDoseBfacs");
                    if (NavDoseBfacs != null)
                        GridDoseBfacs = CubicGrid.Load(NavDoseBfacs);

                    XPathNavigator NavDoseBfacsDelta = Reader.SelectSingleNode("//GridDoseBfacsDelta");
                    if (NavDoseBfacsDelta != null)
                        GridDoseBfacsDelta = CubicGrid.Load(NavDoseBfacsDelta);

                    XPathNavigator NavDoseBfacsAngle = Reader.SelectSingleNode("//GridDoseBfacsAngle");
                    if (NavDoseBfacsAngle != null)
                        GridDoseBfacsAngle = CubicGrid.Load(NavDoseBfacsAngle);

                    XPathNavigator NavDoseWeights = Reader.SelectSingleNode("//GridDoseWeights");
                    if (NavDoseWeights != null)
                        GridDoseWeights = CubicGrid.Load(NavDoseWeights);

                    XPathNavigator NavLocationBfacs = Reader.SelectSingleNode("//GridLocationBfacs");
                    if (NavLocationBfacs != null)
                        GridLocationBfacs = CubicGrid.Load(NavLocationBfacs);

                    XPathNavigator NavLocationWeights = Reader.SelectSingleNode("//GridLocationWeights");
                    if (NavLocationWeights != null)
                        GridLocationWeights = CubicGrid.Load(NavLocationWeights);

                    #endregion
                }
            }
            catch
            {
                return;
            }
        }

        public override void SaveMeta()
        {
            using (XmlTextWriter Writer = new XmlTextWriter(XMLPath, Encoding.Unicode))
            {
                Writer.Formatting = Formatting.Indented;
                Writer.IndentChar = '\t';
                Writer.Indentation = 1;
                Writer.WriteStartDocument();
                Writer.WriteStartElement("TiltSeries");

                #region Attributes

                Writer.WriteAttributeString("AreAnglesInverted", AreAnglesInverted.ToString());
                Writer.WriteAttributeString("PlaneNormal", PlaneNormal.ToString());

                Writer.WriteAttributeString("Bfactor", GlobalBfactor.ToString(CultureInfo.InvariantCulture));
                Writer.WriteAttributeString("Weight", GlobalWeight.ToString(CultureInfo.InvariantCulture));

                Writer.WriteAttributeString("MagnificationCorrection", MagnificationCorrection.ToString());

                Writer.WriteAttributeString("UnselectFilter", UnselectFilter.ToString());
                Writer.WriteAttributeString("UnselectManual", UnselectManual.ToString());
                Writer.WriteAttributeString("CTFResolutionEstimate", CTFResolutionEstimate.ToString(CultureInfo.InvariantCulture));

                #endregion

                #region Per-tilt propertries

                Writer.WriteStartElement("Angles");
                Writer.WriteString(string.Join("\n", Angles.Select(v => v.ToString(CultureInfo.InvariantCulture))));
                Writer.WriteEndElement();

                Writer.WriteStartElement("Dose");
                Writer.WriteString(string.Join("\n", Dose.Select(v => v.ToString(CultureInfo.InvariantCulture))));
                Writer.WriteEndElement();

                Writer.WriteStartElement("UseTilt");
                Writer.WriteString(string.Join("\n", UseTilt.Select(v => v.ToString())));
                Writer.WriteEndElement();

                Writer.WriteStartElement("AxisAngle");
                Writer.WriteString(string.Join("\n", TiltAxisAngles.Select(v => v.ToString())));
                Writer.WriteEndElement();

                Writer.WriteStartElement("AxisOffsetX");
                Writer.WriteString(string.Join("\n", TiltAxisOffsetX.Select(v => v.ToString())));
                Writer.WriteEndElement();

                Writer.WriteStartElement("AxisOffsetY");
                Writer.WriteString(string.Join("\n", TiltAxisOffsetY.Select(v => v.ToString())));
                Writer.WriteEndElement();

                Writer.WriteStartElement("MoviePath");
                Writer.WriteString(string.Join("\n", TiltMoviePaths.Select(v => v.ToString())));
                Writer.WriteEndElement();

                #endregion

                #region CTF fitting-related

                foreach (float2[] ps1d in TiltPS1D)
                {
                    Writer.WriteStartElement("TiltPS1D");
                    XMLHelper.WriteAttribute(Writer, "ID", TiltPS1D.IndexOf(ps1d));
                    Writer.WriteString(string.Join(";", ps1d.Select(v => v.X.ToString(CultureInfo.InvariantCulture) + "|" + v.Y.ToString(CultureInfo.InvariantCulture))));
                    Writer.WriteEndElement();
                }

                foreach (Cubic1D simulatedScale in TiltSimulatedScale)
                {
                    Writer.WriteStartElement("TiltSimulatedScale");
                    XMLHelper.WriteAttribute(Writer, "ID", TiltSimulatedScale.IndexOf(simulatedScale));
                    Writer.WriteString(string.Join(";",
                                                   simulatedScale.Data.Select(v => v.X.ToString(CultureInfo.InvariantCulture) +
                                                                                   "|" +
                                                                                   v.Y.ToString(CultureInfo.InvariantCulture))));
                    Writer.WriteEndElement();
                }

                if (PS1D != null)
                {
                    Writer.WriteStartElement("PS1D");
                    Writer.WriteString(string.Join(";", PS1D.Select(v => v.X.ToString(CultureInfo.InvariantCulture) + "|" + v.Y.ToString(CultureInfo.InvariantCulture))));
                    Writer.WriteEndElement();
                }

                if (SimulatedScale != null)
                {
                    Writer.WriteStartElement("SimulatedScale");
                    Writer.WriteString(string.Join(";",
                                                   SimulatedScale.Data.Select(v => v.X.ToString(CultureInfo.InvariantCulture) +
                                                                                    "|" +
                                                                                    v.Y.ToString(CultureInfo.InvariantCulture))));
                    Writer.WriteEndElement();
                }

                if (OptionsCTF != null)
                {
                    Writer.WriteStartElement("OptionsCTF");
                    OptionsCTF.WriteToXML(Writer);
                    Writer.WriteEndElement();
                }

                Writer.WriteStartElement("CTF");
                CTF.WriteToXML(Writer);
                Writer.WriteEndElement();

                #endregion

                #region Grids

                Writer.WriteStartElement("GridCTF");
                GridCTFDefocus.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridCTFDefocusDelta");
                GridCTFDefocusDelta.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridCTFDefocusAngle");
                GridCTFDefocusAngle.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridCTFPhase");
                GridCTFPhase.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridMovementX");
                GridMovementX.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridMovementY");
                GridMovementY.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridVolumeWarpX");
                GridVolumeWarpX.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridVolumeWarpY");
                GridVolumeWarpY.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridVolumeWarpZ");
                GridVolumeWarpZ.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridAngleX");
                GridAngleX.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridAngleY");
                GridAngleY.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridAngleZ");
                GridAngleZ.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridDoseBfacs");
                GridDoseBfacs.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridDoseBfacsDelta");
                GridDoseBfacsDelta.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridDoseBfacsAngle");
                GridDoseBfacsAngle.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridDoseWeights");
                GridDoseWeights.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridLocationBfacs");
                GridLocationBfacs.Save(Writer);
                Writer.WriteEndElement();

                Writer.WriteStartElement("GridLocationWeights");
                GridLocationWeights.Save(Writer);
                Writer.WriteEndElement();

                #endregion

                Writer.WriteEndElement();
                Writer.WriteEndDocument();
            }
        }

        #endregion

        #region Hashes

        public override string GetDataHash()
        {
            FileInfo Info = new FileInfo(DirectoryName + TiltMoviePaths[0]);
            byte[] DataBytes = new byte[Math.Min(1 << 19, Info.Length)];
            using (BinaryReader Reader = new BinaryReader(File.OpenRead(DirectoryName + TiltMoviePaths[0])))
            {
                Reader.Read(DataBytes, 0, DataBytes.Length);
            }

            DataBytes = Helper.Combine(Helper.ToBytes(RootName.ToCharArray()), DataBytes);

            return MathHelper.GetSHA1(DataBytes);
        }

        public override string GetProcessingHash()
        {
            List<byte[]> Arrays = new List<byte[]>();

            if (CTF != null)
            {
                Arrays.Add(Helper.ToBytes(new[]
                {
                    CTF.Amplitude,
                    CTF.Bfactor,
                    CTF.Cc,
                    CTF.Cs,
                    CTF.Defocus,
                    CTF.DefocusAngle,
                    CTF.DefocusDelta,
                    CTF.EnergySpread,
                    CTF.IllumAngle,
                    CTF.PhaseShift,
                    CTF.PixelSize,
                    CTF.PixelSizeAngle,
                    CTF.PixelSizeDeltaPercent,
                    CTF.Scale,
                    CTF.Voltage
                }));
                if (CTF.ZernikeCoeffsEven != null)
                    Arrays.Add(Helper.ToBytes(CTF.ZernikeCoeffsEven));
                if (CTF.ZernikeCoeffsOdd != null)
                    Arrays.Add(Helper.ToBytes(CTF.ZernikeCoeffsOdd));
            }
            #region Grids

            if (GridCTFDefocus != null)
            {
                Arrays.Add(GridCTFDefocus.Dimensions);
                Arrays.Add(Helper.ToBytes(GridCTFDefocus.FlatValues));
            }

            if (GridCTFPhase != null)
            {
                Arrays.Add(GridCTFPhase.Dimensions);
                Arrays.Add(Helper.ToBytes(GridCTFPhase.FlatValues));
            }

            if (GridMovementX != null)
            {
                Arrays.Add(GridMovementX.Dimensions);
                Arrays.Add(Helper.ToBytes(GridMovementX.FlatValues));
            }

            if (GridMovementY != null)
            {
                Arrays.Add(GridMovementY.Dimensions);
                Arrays.Add(Helper.ToBytes(GridMovementY.FlatValues));
            }

            if (GridVolumeWarpX != null)
            {
                Arrays.Add(GridVolumeWarpX.Dimensions);
                Arrays.Add(Helper.ToBytes(GridVolumeWarpX.Values));
            }

            if (GridVolumeWarpY != null)
            {
                Arrays.Add(GridVolumeWarpY.Dimensions);
                Arrays.Add(Helper.ToBytes(GridVolumeWarpY.Values));
            }

            if (GridVolumeWarpZ != null)
            {
                Arrays.Add(GridVolumeWarpZ.Dimensions);
                Arrays.Add(Helper.ToBytes(GridVolumeWarpZ.Values));
            }

            if (GridAngleX != null)
            {
                Arrays.Add(GridAngleX.Dimensions);
                Arrays.Add(Helper.ToBytes(GridAngleX.FlatValues));
            }

            if (GridAngleY != null)
            {
                Arrays.Add(GridAngleY.Dimensions);
                Arrays.Add(Helper.ToBytes(GridAngleY.FlatValues));
            }

            if (GridAngleZ != null)
            {
                Arrays.Add(GridAngleZ.Dimensions);
                Arrays.Add(Helper.ToBytes(GridAngleZ.FlatValues));
            }

            if (GridCTFDefocusAngle != null)
            {
                Arrays.Add(GridCTFDefocusAngle.Dimensions);
                Arrays.Add(Helper.ToBytes(GridCTFDefocusAngle.FlatValues));
            }

            if (GridCTFDefocusDelta != null)
            {
                Arrays.Add(GridCTFDefocusDelta.Dimensions);
                Arrays.Add(Helper.ToBytes(GridCTFDefocusDelta.FlatValues));
            }

            if (GridDoseBfacs != null)
            {
                Arrays.Add(GridDoseBfacs.Dimensions);
                Arrays.Add(Helper.ToBytes(GridDoseBfacs.FlatValues));
            }

            #endregion

            Arrays.Add(Helper.ToBytes(TiltAxisAngles));
            Arrays.Add(Helper.ToBytes(TiltAxisOffsetX));
            Arrays.Add(Helper.ToBytes(TiltAxisOffsetX));
            Arrays.Add(Helper.ToBytes(Angles));
            Arrays.Add(Helper.ToBytes(Dose));
            Arrays.Add(Helper.ToBytes(UseTilt));

            foreach (var moviePath in TiltMoviePaths)
            {
                Movie TiltMovie = new Movie(DirectoryName + moviePath);
                Arrays.Add(Helper.ToBytes(TiltMovie.GetProcessingHash().ToCharArray()));
            }

            byte[] ArraysCombined = Helper.Combine(Arrays);
            return MathHelper.GetSHA1(ArraysCombined);
        }

        #endregion

        #region Experimental

        public Image SimulateTiltSeries(TomoProcessingOptionsBase options, int3 stackDimensions, float3[][] particleOrigins, float3[][] particleAngles, int[] nParticles, Projector[] references)
        {
            VolumeDimensionsPhysical = options.DimensionsPhysical;
            float BinnedPixelSize = (float)options.BinnedPixelSizeMean;

            Image SimulatedStack = new Image(stackDimensions);

            // Extract images, mask and resize them, create CTFs

            for (int iref = 0; iref < references.Length; iref++)
            {
                int Size = references[iref].Dims.X;
                int3 Dims = new int3(Size);

                Image CTFCoords = CTF.GetCTFCoords(Size, Size);

                #region For each particle, create CTFs and projections, and insert them into the simulated tilt series

                for (int p = 0; p < nParticles[iref]; p++)
                {
                    float3 ParticleCoords = particleOrigins[iref][p];

                    float3[] Positions = GetPositionInAllTilts(ParticleCoords);
                    for (int i = 0; i < Positions.Length; i++)
                        Positions[i] /= BinnedPixelSize;

                    float3[] Angles = GetParticleAngleInAllTilts(ParticleCoords, particleAngles[iref][p]);

                    Image ParticleCTFs = GetCTFsForOneParticle(options, ParticleCoords, CTFCoords, null);

                    // Make projections

                    float3[] ImageShifts = new float3[NTilts];

                    for (int t = 0; t < NTilts; t++)
                    {
                        ImageShifts[t] = new float3(Positions[t].X - (int)Positions[t].X, // +diff because we are shifting the projections into experimental data frame
                                                    Positions[t].Y - (int)Positions[t].Y,
                                                    Positions[t].Z - (int)Positions[t].Z);
                    }

                    Image ProjectionsFT = references[iref].Project(new int2(Size), Angles);

                    ProjectionsFT.ShiftSlices(ImageShifts);
                    ProjectionsFT.Multiply(ParticleCTFs);
                    ParticleCTFs.Dispose();

                    Image Projections = ProjectionsFT.AsIFFT();
                    ProjectionsFT.Dispose();

                    Projections.RemapFromFT();


                    // Insert projections into tilt series

                    for (int t = 0; t < NTilts; t++)
                    {
                        int2 IntPosition = new int2((int)Positions[t].X, (int)Positions[t].Y) - Size / 2;

                        float[] SimulatedData = SimulatedStack.GetHost(Intent.Write)[t];

                        float[] ImageData = Projections.GetHost(Intent.Read)[t];
                        for (int y = 0; y < Size; y++)
                        {
                            int PosY = y + IntPosition.Y;
                            if (PosY < 0 || PosY >= stackDimensions.Y)
                                continue;

                            for (int x = 0; x < Size; x++)
                            {
                                int PosX = x + IntPosition.X;
                                if (PosX < 0 || PosX >= stackDimensions.X)
                                    continue;

                                SimulatedData[PosY * SimulatedStack.Dims.X + PosX] += ImageData[y * Size + x];
                            }
                        }
                    }

                    Projections.Dispose();
                }

                #endregion

                CTFCoords.Dispose();
            }

            return SimulatedStack;
        }

        #endregion
    }

    [Serializable]
    public abstract class TomoProcessingOptionsBase : ProcessingOptionsBase
    {
        public float3 DimensionsPhysical => Dimensions * (float)PixelSizeMean;

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((TomoProcessingOptionsBase)obj);
        }

        protected bool Equals(TomoProcessingOptionsBase other)
        {
            return base.Equals(other) &&
                   Dimensions == other.Dimensions;
        }

        public static bool operator ==(TomoProcessingOptionsBase left, TomoProcessingOptionsBase right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(TomoProcessingOptionsBase left, TomoProcessingOptionsBase right)
        {
            return !Equals(left, right);
        }
    }

    [Serializable]
    public class ProcessingOptionsTomoFullReconstruction : TomoProcessingOptionsBase
    {
        [WarpSerializable]
        public bool OverwriteFiles { get; set; }

        [WarpSerializable]
        public bool Invert { get; set; }

        [WarpSerializable]
        public bool Normalize { get; set; }

        [WarpSerializable]
        public bool DoDeconv { get; set; }

        [WarpSerializable]
        public decimal DeconvStrength { get; set; }

        [WarpSerializable]
        public decimal DeconvFalloff { get; set; }

        [WarpSerializable]
        public decimal DeconvHighpass { get; set; }

        [WarpSerializable]
        public int SubVolumeSize { get; set; }

        [WarpSerializable]
        public decimal SubVolumePadding { get; set; }

        [WarpSerializable]
        public bool PrepareDenoising { get; set; }

        [WarpSerializable]
        public bool KeepOnlyFullVoxels { get; set; }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((ProcessingOptionsTomoFullReconstruction)obj);
        }

        protected bool Equals(ProcessingOptionsTomoFullReconstruction other)
        {
            return base.Equals(other) &&
                   Invert == other.Invert &&
                   Normalize == other.Normalize &&
                   DoDeconv == other.DoDeconv &&
                   DeconvStrength == other.DeconvStrength &&
                   DeconvFalloff == other.DeconvFalloff &&
                   DeconvHighpass == other.DeconvHighpass &&
                   SubVolumeSize == other.SubVolumeSize &&
                   SubVolumePadding == other.SubVolumePadding &&
                   PrepareDenoising == other.PrepareDenoising &&
                   KeepOnlyFullVoxels == other.KeepOnlyFullVoxels;
        }

        public static bool operator ==(ProcessingOptionsTomoFullReconstruction left, ProcessingOptionsTomoFullReconstruction right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(ProcessingOptionsTomoFullReconstruction left, ProcessingOptionsTomoFullReconstruction right)
        {
            return !Equals(left, right);
        }
    }

    [Serializable]
    public class ProcessingOptionsTomoFullMatch : TomoProcessingOptionsBase
    {
        [WarpSerializable]
        public bool OverwriteFiles { get; set; }

        [WarpSerializable]
        public int SubVolumeSize { get; set; }

        [WarpSerializable]
        public string TemplateName { get; set; }

        [WarpSerializable]
        public decimal TemplatePixel { get; set; }

        [WarpSerializable]
        public decimal TemplateDiameter { get; set; }

        [WarpSerializable]
        public decimal TemplateFraction { get; set; }

        [WarpSerializable]
        public bool KeepOnlyFullVoxels { get; set; }

        [WarpSerializable]
        public bool WhitenSpectrum { get; set; }

        [WarpSerializable]
        public string Symmetry { get; set; }

        [WarpSerializable]
        public int HealpixOrder { get; set; }

        [WarpSerializable]
        public int Supersample { get; set; }

        [WarpSerializable]
        public int NResults { get; set; }

        [WarpSerializable]
        public bool ReuseCorrVolumes { get; set; }
    }

    [Serializable]
    public class ProcessingOptionsTomoSubReconstruction : TomoProcessingOptionsBase
    {
        [WarpSerializable]
        public string Suffix { get; set; }
        [WarpSerializable]
        public int BoxSize { get; set; }
        [WarpSerializable]
        public int ParticleDiameter { get; set; }
        [WarpSerializable]
        public bool Invert { get; set; }
        [WarpSerializable]
        public bool NormalizeInput { get; set; }
        [WarpSerializable]
        public bool NormalizeOutput { get; set; }
        [WarpSerializable]
        public bool PrerotateParticles { get; set; }
        [WarpSerializable]
        public bool DoLimitDose { get; set; }
        [WarpSerializable]
        public int NTilts { get; set; }
        [WarpSerializable]
        public bool MakeSparse { get; set; }
    }
}
