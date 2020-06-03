﻿using CommandLine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Warp;
using Warp.Sociology;
using Warp.Tools;

namespace EstimateWeights
{
    class EstimateWeights
    {
        static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            Options Options = new Options();
            string WorkingDirectory;

            Dictionary<int, string> MaskOverrideNames = new Dictionary<int, string>();

            if (!Debugger.IsAttached)
            {
                Parser.Default.ParseArguments<Options>(args).WithParsed<Options>(opts => Options = opts);
                WorkingDirectory = Environment.CurrentDirectory + "/";
            }
            else
            {
                Options.PopulationPath = "population/fine_align.population";
                Options.MinResolution = 15;
                Options.DoTiltFrames = true;
                Options.FitAnisotropy = false;
                Options.ResolveLocation = false;
                Options.ResolveFrames = true;
                Options.ResolveItems = true;
                Options.GridWidth = 5;
                Options.GridHeight = 5;

                WorkingDirectory = @"Y:\data\dtegunov\20190731_2Dvs3D\3D\";
            }

            Options.PopulationPath = Path.Combine(WorkingDirectory, Options.PopulationPath);
            string RefinementDirectory = Path.Combine(Helper.PathToFolder(Options.PopulationPath), "refinement_temp");

            if (!Options.ResolveFrames && !Options.ResolveItems && !Options.ResolveLocation)
                throw new Exception("No dimension chosen to resolve over.");

            if (Options.ResolveLocation && (Options.ResolveFrames || Options.ResolveItems))
                throw new Exception("Spatial resolution cannot be combined with other dimensions in the same run. Please execute separately with different dimensions.");

            Population P = new Population(Options.PopulationPath);
            DataSource[] Sources = P.Sources.ToArray();

            float PixelSize = (float)P.Species[0].PixelSize;

            foreach (var source in Sources)
            {
                List<Movie> ItemsWithData = new List<Movie>();

                foreach (var item in source.Files.Values)
                {
                    if (Options.DoTiltFrames)
                    {
                        bool SomethingMissing = false;
                        string ItemPath = Path.Combine(source.FolderPath, item);
                        TiltSeries Series = new TiltSeries(ItemPath);

                        for (int t = 0; t < Series.NTilts; t++)
                        {
                            if (!File.Exists(Path.Combine(RefinementDirectory, $"{Helper.PathToName(item)}_tilt{t:D3}_fsc.mrc")))
                            {
                                SomethingMissing = true;
                                break;
                            }
                        }

                        if (SomethingMissing)
                            continue;

                        ItemsWithData.Add(Series);
                    }
                    else
                    {
                        if (Options.ResolveFrames || Options.ResolveItems)
                        {
                            if (!File.Exists(Path.Combine(RefinementDirectory, Helper.PathToName(item) + "_fsc.mrc")))
                                continue;
                        }
                        else if (Options.ResolveLocation)
                        {
                            if (!File.Exists(Path.Combine(RefinementDirectory, Helper.PathToName(item) + "_fscparticles.mrc")))
                                continue;
                            if (!File.Exists(Path.Combine(RefinementDirectory, Helper.PathToName(item) + "_fscparticles.star")))
                                continue;
                        }

                        string ItemPath = Path.Combine(source.FolderPath, item);

                        if (item.Contains(".tomostar"))
                            ItemsWithData.Add(new TiltSeries(ItemPath));
                        else
                            ItemsWithData.Add(new Movie(ItemPath));
                    }
                }

                if (ItemsWithData.Count == 0)
                    Console.WriteLine($"No items with FSC data found for source {source.Name}!");

                if (Options.DoTiltFrames)
                {
                    for (int t = 0; t < ((TiltSeries)ItemsWithData[0]).NTilts; t++)
                    {
                        int2 Dims;
                        int MaxFrames = 1;
                        {
                            string FSCPath = Path.Combine(RefinementDirectory, $"{ItemsWithData[0].RootName}_tilt{t:D3}_fsc.mrc");
                            Image FSC = Image.FromFile(FSCPath);
                            Dims = new int2(FSC.Dims.Y);
                            MaxFrames = FSC.Dims.Z / 3;
                            FSC.Dispose();
                        }

                        List<float[]> AllAB = new List<float[]>();
                        List<float[]> AllA2 = new List<float[]>();
                        List<float[]> AllB2 = new List<float[]>();

                        foreach (var parentItem in ItemsWithData.Select(v => (TiltSeries)v))
                        {
                            string MoviePath = Path.Combine(parentItem.DirectoryName, parentItem.TiltMoviePaths[t]);
                            Movie item = new Movie(MoviePath);

                            {
                                string FSCPath = Path.Combine(RefinementDirectory, $"{parentItem.RootName}_tilt{t:D3}_fsc.mrc");

                                Image FSC = Image.FromFile(FSCPath);
                                float[][] FSCData = FSC.GetHost(Intent.Read);

                                for (int z = 0; z < FSCData.Length / 3; z++)
                                {
                                    if (AllAB.Count <= z)
                                    {
                                        AllAB.Add(FSCData[z * 3 + 0]);
                                        AllA2.Add(FSCData[z * 3 + 1]);
                                        AllB2.Add(FSCData[z * 3 + 2]);
                                    }
                                    else
                                    {
                                        for (int i = 0; i < FSCData[0].Length; i++)
                                        {
                                            AllAB[z][i] += FSCData[z * 3 + 0][i];
                                            AllA2[z][i] += FSCData[z * 3 + 1][i];
                                            AllB2[z][i] += FSCData[z * 3 + 2][i];
                                        }
                                    }
                                }

                                FSC.Dispose();
                            }
                        }

                        int NItems = AllAB.Count;

                        Image CorrAB = new Image(AllAB.ToArray(), new int3(Dims.X, Dims.Y, NItems), true);
                        Image CorrA2 = new Image(AllA2.ToArray(), new int3(Dims.X, Dims.Y, NItems), true);
                        Image CorrB2 = new Image(AllB2.ToArray(), new int3(Dims.X, Dims.Y, NItems), true);

                        Image CTFWeights = CorrAB.GetCopyGPU();
                        CTFWeights.Fill(1f);

                        (float[] ResultScales, float3[] ResultBfactors) = FSC.FitBFactors2D(CorrAB, CorrA2, CorrB2, CTFWeights, PixelSize, Options.MinResolution, Options.FitAnisotropy);

                        CorrAB.Dispose();
                        CorrA2.Dispose();
                        CorrB2.Dispose();
                        CTFWeights.Dispose();

                        float MaxScale = MathHelper.Max(ResultScales);
                        ResultScales = ResultScales.Select(v => v / MaxScale).ToArray();

                        List<float> BfacsSorted = new List<float>(ResultBfactors.Select(v => v.X));
                        float MaxBfac = MathHelper.Max(BfacsSorted);
                        //float MaxBfac = MathHelper.Max(ResultBfactors.Select(v => v.X));
                        ResultBfactors = ResultBfactors.Select(v => new float3(Math.Min(0, v.X - MaxBfac), v.Y, v.Z)).ToArray();

                        List<float3> TableRows = new List<float3>();

                        foreach (var parentItem in ItemsWithData.Select(v => (TiltSeries)v))
                        {
                            string MoviePath = Path.Combine(parentItem.DirectoryName, parentItem.TiltMoviePaths[t]);
                            Movie item = new Movie(MoviePath);

                            int InThisItem = MaxFrames;

                            float[] Weights = ResultScales;
                            float[] Bfacs = ResultBfactors.Select(v => v.X).ToArray();
                            float[] BfacsDelta = ResultBfactors.Select(v => v.Y).ToArray();
                            float[] BfacsAngle = ResultBfactors.Select(v => v.Z).ToArray();

                            (int MaxIndex, float MaxB) = MathHelper.MaxElement(Bfacs);
                            TableRows.Add(new float3((float)item.CTF.Defocus,
                                                     Weights[MaxIndex],
                                                     MaxB));

                            item.GridDoseWeights = new CubicGrid(new int3(1, 1, InThisItem), Weights);

                            item.GridDoseBfacs = new CubicGrid(new int3(1, 1, InThisItem), Bfacs);
                            item.GridDoseBfacsDelta = new CubicGrid(new int3(1, 1, InThisItem), BfacsDelta);
                            item.GridDoseBfacsAngle = new CubicGrid(new int3(1, 1, InThisItem), BfacsAngle);

                            item.SaveMeta();
                        }
                    }
                }
                else if (Options.ResolveFrames || Options.ResolveItems)
                {
                    int2 Dims;
                    int MaxFrames = 1;
                    {
                        string FSCPath = Path.Combine(RefinementDirectory, ItemsWithData[0].RootName + "_fsc.mrc");
                        Image FSC = Image.FromFile(FSCPath);
                        Dims = new int2(FSC.Dims.Y);
                        MaxFrames = FSC.Dims.Z / 3;
                        FSC.Dispose();
                    }

                    List<float[]> AllAB = new List<float[]>();// (Helper.ArrayOfFunction(i => new float[Dims.ElementsFFT()], MaxFrames));
                    List<float[]> AllA2 = new List<float[]>();// (Helper.ArrayOfFunction(i => new float[Dims.ElementsFFT()], MaxFrames));
                    List<float[]> AllB2 = new List<float[]>();// (Helper.ArrayOfFunction(i => new float[Dims.ElementsFFT()], MaxFrames));

                    int NDone = 0;
                    foreach (var item in ItemsWithData)
                    {
                        //if (NDone++ >= 30)
                        //    break;

                        string FSCPath = Path.Combine(RefinementDirectory, item.RootName + "_fsc.mrc");

                        Image FSC = Image.FromFile(FSCPath);
                        float[][] FSCData = FSC.GetHost(Intent.Read);

                        for (int z = 0; z < FSCData.Length / 3; z++)
                        {
                            AllAB.Add(FSCData[z * 3 + 0]);
                            AllA2.Add(FSCData[z * 3 + 1]);
                            AllB2.Add(FSCData[z * 3 + 2]);
                            //for (int i = 0; i < FSCData[0].Length; i++)
                            //{
                            //    AllAB[z][i] += FSCData[z * 3 + 0][i];
                            //    AllA2[z][i] += FSCData[z * 3 + 1][i];
                            //    AllB2[z][i] += FSCData[z * 3 + 2][i];
                            //}
                        }

                        FSC.Dispose();
                    }

                    int NItems = AllAB.Count;

                    Image CorrAB = new Image(AllAB.ToArray(), new int3(Dims.X, Dims.Y, NItems), true);
                    Image CorrA2 = new Image(AllA2.ToArray(), new int3(Dims.X, Dims.Y, NItems), true);
                    Image CorrB2 = new Image(AllB2.ToArray(), new int3(Dims.X, Dims.Y, NItems), true);

                    Image CTFWeights = CorrAB.GetCopyGPU();
                    CTFWeights.Fill(1f);

                    (float[] ResultScales, float3[] ResultBfactors) = FSC.FitBFactors2D(CorrAB, CorrA2, CorrB2, CTFWeights, PixelSize, Options.MinResolution, Options.FitAnisotropy);

                    float MaxScale = MathHelper.Max(ResultScales);
                    ResultScales = ResultScales.Select(v => v / MaxScale).ToArray();

                    List<float> BfacsSorted = new List<float>(ResultBfactors.Select(v => v.X));
                    BfacsSorted.Sort();
                    float MaxBfac = BfacsSorted[(int)(BfacsSorted.Count * 0.99f)];
                    //float MaxBfac = MathHelper.Max(ResultBfactors.Select(v => v.X));
                    ResultBfactors = ResultBfactors.Select(v => new float3(Math.Min(0, v.X - MaxBfac), v.Y, v.Z)).ToArray();

                    List<float3> TableRows = new List<float3>();

                    int NFramesDone = 0;
                    foreach (var item in ItemsWithData)
                    {
                        int InThisItem = MaxFrames;
                        if (item.GetType() == typeof(TiltSeries))
                            InThisItem = ((TiltSeries)item).NTilts;

                        float[] Weights = ResultScales.Skip(NFramesDone).Take(InThisItem).ToArray();
                        float[] Bfacs = ResultBfactors.Skip(NFramesDone).Take(InThisItem).Select(v => v.X).ToArray();
                        float[] BfacsDelta = ResultBfactors.Skip(NFramesDone).Take(InThisItem).Select(v => v.Y).ToArray();
                        float[] BfacsAngle = ResultBfactors.Skip(NFramesDone).Take(InThisItem).Select(v => v.Z).ToArray();

                        (int MaxIndex, float MaxB) = MathHelper.MaxElement(Bfacs);
                        TableRows.Add(new float3((float)item.CTF.Defocus,
                                                 Weights[MaxIndex],
                                                 MaxB));

                        item.GridDoseWeights = new CubicGrid(new int3(1, 1, InThisItem), Weights);

                        item.GridDoseBfacs = new CubicGrid(new int3(1, 1, InThisItem), Bfacs);
                        item.GridDoseBfacsDelta = new CubicGrid(new int3(1, 1, InThisItem), BfacsDelta);
                        item.GridDoseBfacsAngle = new CubicGrid(new int3(1, 1, InThisItem), BfacsAngle);

                        item.SaveMeta();

                        NFramesDone += InThisItem;
                    }

                    (new Star(TableRows.ToArray(), "wrpDefocus", "wrpMaxWeight", "wrpMaxB")).Save("d_defocus_vs_bfactor.star");
                }
                else if (Options.ResolveLocation)
                {
                    List<List<Movie>> ItemGroups = new List<List<Movie>>();
                    foreach (var item in ItemsWithData)
                        ItemGroups.Add(new List<Movie>() { item });

                    int FSCLength = 0;
                    {
                        string FSCPath = Path.Combine(RefinementDirectory, ItemsWithData[0].RootName + "_fscparticles.mrc");
                        Image FSC = Image.FromFile(FSCPath);
                        FSCLength = FSC.Dims.X;
                        FSC.Dispose();
                    }

                    int NGroupsDone = 0;
                    foreach (var itemGroup in ItemGroups)
                    {
                        int2 DimsGrid = new int2(Options.GridWidth, Options.GridHeight);
                        float2 GridMultiplier = new float2(DimsGrid - 1);
                        float3[][] AllFSCs = Helper.ArrayOfFunction(i => new float3[FSCLength], (int)DimsGrid.Elements());
                        float[][] AllCTFs = Helper.ArrayOfFunction(i => Helper.ArrayOfConstant(1f, FSCLength), (int)DimsGrid.Elements());

                        foreach (var item in itemGroup)
                        {
                            string FSCPath = Path.Combine(RefinementDirectory, item.RootName + "_fscparticles.mrc");
                            string StarPath = Path.Combine(RefinementDirectory, item.RootName + "_fscparticles.star");

                            Image FSC = Image.FromFile(FSCPath);
                            float[] FSCData = FSC.GetHost(Intent.Read)[0];

                            Star TableIn = new Star(StarPath);
                            float2[] Coords = TableIn.GetFloat2("wrpNormCoordinateX", "wrpNormCoordinateY");

                            if (Coords.Length * 3 != FSC.Dims.Y)
                                throw new Exception($"Number of particles does not match number of FSC lines");

                            for (int p = 0; p < Coords.Length; p++)
                            {
                                int2 CoordsRounded = new int2((int)Math.Round(Coords[p].X * GridMultiplier.X),
                                                              (int)Math.Round(Coords[p].Y * GridMultiplier.Y));
                                int FSCID = CoordsRounded.Y * DimsGrid.X + CoordsRounded.X;
                                float3[] LocalFSC = AllFSCs[FSCID];

                                for (int i = 0; i < FSCLength; i++)
                                    LocalFSC[i] += new float3(FSCData[(p * 3 + 0) * FSCLength + i],
                                                              FSCData[(p * 3 + 1) * FSCLength + i],
                                                              FSCData[(p * 3 + 2) * FSCLength + i]);
                            }

                            FSC.Dispose();
                        }

                        try
                        {
                            float2[] PositionFits = FSC.FitBFactors(AllFSCs, AllCTFs, PixelSize, Options.MinResolution);

                            float MaxWeight = MathHelper.Max(PositionFits.Select(v => v.X));
                            float MaxBfactor = MathHelper.Max(PositionFits.Select(v => v.Y));

                            PositionFits = PositionFits.Select(v => new float2(v.X / MaxWeight, v.Y - MaxBfactor)).ToArray();

                            Image Fitted = new Image(new[] { PositionFits.Select(v => v.X).ToArray(), PositionFits.Select(v => v.Y).ToArray() }, new int3(DimsGrid.X, DimsGrid.Y, 2));
                            Fitted.WriteMRC($"d_fitted_{NGroupsDone}.mrc", true);

                            foreach (var item in itemGroup)
                            {
                                item.GridLocationWeights = new CubicGrid(new int3(DimsGrid.X, DimsGrid.Y, 1), PositionFits.Select(v => v.X).ToArray());
                                item.GridLocationBfacs = new CubicGrid(new int3(DimsGrid.X, DimsGrid.Y, 1), PositionFits.Select(v => v.Y).ToArray());

                                item.SaveMeta();
                            }
                        }
                        catch { }

                        NGroupsDone++;
                    }
                }
            }
        }
    }
}
