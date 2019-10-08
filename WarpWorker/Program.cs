﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Warp;
using Warp.Headers;
using Warp.Sociology;
using Warp.Tools;

namespace WarpWorker
{
    class Program
    {
        static int DeviceID = 0;
        static string PipeName = "";

        static Thread Heartbeat;
        static bool Terminating = false;

        static NamedPipeClientStream PipeReceive;
        static NamedPipeClientStream PipeSend;

        static BinaryFormatter Formatter;

        static Image GainRef = null;
        static int2 HeaderlessDims = new int2(2);
        static long HeaderlessOffset = 0;
        static string HeaderlessType = "float32";

        static float[][] RawLayers = null;

        static string OriginalStackOwner = "";
        static Image OriginalStack = null;

        static Population MPAPopulation = null;

        static void Main(string[] args)
        {
            //if (!Debugger.IsAttached)
            //    Debugger.Launch();

            if (args.Length < 2)
                return;

            DeviceID = int.Parse(args[0]) % GPU.GetDeviceCount();
            PipeName = args[1];

            GPU.SetDevice(DeviceID);

            Console.WriteLine($"Running on GPU #{DeviceID} ({GPU.GetFreeMemory(DeviceID)} MB free) through {PipeName}\n");
            
            Formatter = new BinaryFormatter();

            Heartbeat = new Thread(new ThreadStart(() =>
            {
                while (true)
                    try
                    {
                        NamedPipeClientStream PipeHeartbeat = new NamedPipeClientStream(".", PipeName + "_heartbeat", PipeDirection.In);
                        PipeHeartbeat.Connect(5000);

                        PipeHeartbeat.Dispose();
                    }
                    catch
                    {
                        if (!Terminating)
                            Process.GetCurrentProcess().Kill();
                    }
            }));
            Heartbeat.Start();

            while (true)
            {
                PipeReceive = new NamedPipeClientStream(".", PipeName + "_out", PipeDirection.In);
                PipeReceive.Connect();

                NamedSerializableObject Command = (NamedSerializableObject)Formatter.Deserialize(PipeReceive);

                PipeReceive.Dispose();

                Console.WriteLine($"Received \"{Command.Name}\", with {Command.Content.Length} arguments, for GPU #{GPU.GetDevice()}, {GPU.GetFreeMemory(DeviceID)} MB free");

                try
                {
                    Stopwatch Watch = new Stopwatch();
                    Watch.Start();

                    if (Command.Name == "Exit")
                    {
                        SendSuccessStatus(true);
                        Process.GetCurrentProcess().Kill();

                        return;
                    }
                    else if (Command.Name == "Ping")
                    {
                        Console.WriteLine("Ping!");
                    }
                    else if (Command.Name == "SetHeaderlessParams")
                    {
                        HeaderlessDims = (int2)Command.Content[0];
                        HeaderlessOffset = (long)Command.Content[1];
                        HeaderlessType = (string)Command.Content[2];

                        Console.WriteLine($"Set headerless parameters to {HeaderlessDims}, {HeaderlessOffset}, {HeaderlessType}");
                    }
                    else if (Command.Name == "LoadGainRef")
                    {
                        GainRef?.Dispose();

                        string Path = (string)Command.Content[0];
                        bool FlipX = (bool)Command.Content[1];
                        bool FlipY = (bool)Command.Content[2];
                        bool Transpose = (bool)Command.Content[3];

                        if (!string.IsNullOrEmpty(Path))
                        {
                            GainRef = LoadAndPrepareGainReference(Path, FlipX, FlipY, Transpose);
                        }

                        Console.WriteLine($"Loaded gain reference: {GainRef}, {FlipX}, {FlipY}, {Transpose}");
                    }
                    else if (Command.Name == "LoadStack")
                    {
                        OriginalStack?.Dispose();

                        string Path = (string)Command.Content[0];
                        decimal ScaleFactor = (decimal)Command.Content[1];

                        OriginalStack = LoadAndPrepareStack(Path, GainRef, ScaleFactor);
                        OriginalStackOwner = Helper.PathToNameWithExtension(Path);

                        Console.WriteLine($"Loaded stack: {OriginalStack}, {ScaleFactor}");
                    }
                    else if (Command.Name == "MovieProcessCTF")
                    {
                        string Path = (string)Command.Content[0];
                        ProcessingOptionsMovieCTF Options = (ProcessingOptionsMovieCTF)Command.Content[1];

                        if (Helper.PathToNameWithExtension(Path) != OriginalStackOwner)
                            throw new Exception("Currently loaded stack doesn't match the movie requested for processing!");

                        Movie M = new Movie(Path);
                        M.ProcessCTF(OriginalStack, Options);
                        M.SaveMeta();

                        Console.WriteLine($"Processed CTF for {Path}");
                    }
                    else if (Command.Name == "MovieProcessMovement")
                    {
                        string Path = (string)Command.Content[0];
                        ProcessingOptionsMovieMovement Options = (ProcessingOptionsMovieMovement)Command.Content[1];

                        if (Helper.PathToNameWithExtension(Path) != OriginalStackOwner)
                            throw new Exception("Currently loaded stack doesn't match the movie requested for processing!");

                        Movie M = new Movie(Path);
                        M.ProcessShift(OriginalStack, Options);
                        M.SaveMeta();

                        Console.WriteLine($"Processed movement for {Path}");
                    }
                    else if (Command.Name == "MovieExportMovie")
                    {
                        string Path = (string)Command.Content[0];
                        ProcessingOptionsMovieExport Options = (ProcessingOptionsMovieExport)Command.Content[1];

                        if (Helper.PathToNameWithExtension(Path) != OriginalStackOwner)
                            throw new Exception("Currently loaded stack doesn't match the movie requested for processing!");

                        Movie M = new Movie(Path);
                        M.ExportMovie(OriginalStack, Options);
                        M.SaveMeta();

                        Console.WriteLine($"Exported movie for {Path}");
                    }
                    else if (Command.Name == "MovieExportParticles")
                    {
                        string Path = (string)Command.Content[0];
                        ProcessingOptionsParticlesExport Options = (ProcessingOptionsParticlesExport)Command.Content[1];
                        float2[] Coordinates = (float2[])Command.Content[2];

                        if (Helper.PathToNameWithExtension(Path) != OriginalStackOwner)
                            throw new Exception("Currently loaded stack doesn't match the movie requested for processing!");

                        Movie M = new Movie(Path);
                        M.ExportParticles(OriginalStack, Coordinates, Options);
                        M.SaveMeta();

                        Console.WriteLine($"Exported {Coordinates.Length} particles for {Path}");
                    }
                    else if (Command.Name == "TomoProcessCTF")
                    {
                        string Path = (string)Command.Content[0];
                        ProcessingOptionsMovieCTF Options = (ProcessingOptionsMovieCTF)Command.Content[1];

                        TiltSeries T = new TiltSeries(Path);
                        T.ProcessCTFSimultaneous(Options);
                        T.SaveMeta();

                        Console.WriteLine($"Processed CTF for {Path}");
                    }
                    else if (Command.Name == "TomoExportParticles")
                    {
                        string Path = (string)Command.Content[0];
                        ProcessingOptionsTomoSubReconstruction Options = (ProcessingOptionsTomoSubReconstruction)Command.Content[1];
                        float3[] Coordinates = (float3[])Command.Content[2];
                        float3[] Angles = Command.Content[3] != null ? (float3[])Command.Content[3] : null;

                        TiltSeries T = new TiltSeries(Path);
                        T.ReconstructSubtomos(Options, Coordinates, Angles);
                        T.SaveMeta();

                        Console.WriteLine($"Exported {Coordinates.Length} particles for {Path}");
                    }
                    else if (Command.Name == "MPAPreparePopulation")
                    {
                        string Path = (string)Command.Content[0];

                        MPAPopulation = new Population(Path);

                        foreach (var species in MPAPopulation.Species)
                        {
                            Console.Write($"Preparing {species.Name} for refinement... ");

                            species.PrepareRefinementRequisites(true, DeviceID);

                            Console.WriteLine("Done.");
                        }
                    }
                    else if (Command.Name == "MPARefine")
                    {
                        string Path = (string)Command.Content[0];
                        string WorkingDirectory = (string)Command.Content[1];
                        string LogPath = (string)Command.Content[2];
                        ProcessingOptionsMPARefine Options = (ProcessingOptionsMPARefine)Command.Content[3];
                        DataSource Source = (DataSource)Command.Content[4];

                        Movie Item = null;

                        if (Helper.PathToExtension(Path).ToLower() == ".tomostar")
                            Item = new TiltSeries(Path);
                        else
                            Item = new Movie(Path);

                        GPU.SetDevice(DeviceID);

                        Item.PerformMultiParticleRefinement(WorkingDirectory, Options, MPAPopulation.Species.ToArray(), Source, GainRef, (message) =>
                        {
                            Console.WriteLine(message);

                            bool Success = false;
                            int Tries = 0;
                            while (!Success && Tries < 10)
                                try
                                {
                                    using (TextWriter Writer = File.AppendText(LogPath))
                                        Writer.WriteLine(message);
                                    Success = true;
                                }
                                catch
                                {
                                    Thread.Sleep(100);
                                    Tries++;
                                }
                        });

                        Item.SaveMeta();

                        GPU.CheckGPUExceptions();

                        Console.WriteLine($"Finished refining {Item.Name}");
                    }
                    else if (Command.Name == "MPASaveProgress")
                    {
                        string Path = (string)Command.Content[0];

                        MPAPopulation.SaveRefinementProgress(Path);
                    }

                    Watch.Stop();
                    Console.WriteLine((Watch.ElapsedMilliseconds / 1000f).ToString("F3"));

                    Console.WriteLine("");

                    SendSuccessStatus(true);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());

                    File.WriteAllText($"worker_{DeviceID}_crash.txt", e.ToString());

                    //Console.Read();

                    SendSuccessStatus(false);
                }
            }
        }

        static void SendSuccessStatus(bool status)
        {
            PipeSend = new NamedPipeClientStream(".", PipeName + "_in", PipeDirection.Out);
            PipeSend.Connect();

            Formatter.Serialize(PipeSend, new NamedSerializableObject("Success", status));

            PipeSend.Dispose();
        }

        static void CleanUp()
        {
            GainRef?.Dispose();
            OriginalStack?.Dispose();

            Terminating = true;
            Heartbeat.Abort();
        }

        #region Auxiliary

        static Image LoadAndPrepareGainReference(string path, bool flipX, bool flipY, bool transpose)
        {
            Image Gain = Image.FromFilePatient(50, 500,
                                               path,
                                               HeaderlessDims,
                                               (int)HeaderlessOffset,
                                               ImageFormatsHelper.StringToType(HeaderlessType));

            if (flipX)
                Gain = Gain.AsFlippedX();
            if (flipY)
                Gain = Gain.AsFlippedY();
            if (transpose)
                Gain = Gain.AsTransposed();

            return Gain;
        }

        static Image LoadAndPrepareStack(string path, Image imageGain, decimal scaleFactor, int maxThreads = 8)
        {
            Image stack = null;

            MapHeader header = MapHeader.ReadFromFilePatient(50, 500,
                                                             path,
                                                             HeaderlessDims,
                                                             (int)HeaderlessOffset,
                                                             ImageFormatsHelper.StringToType(HeaderlessType));

            if (imageGain != null)
                if (header.Dimensions.X != imageGain.Dims.X || header.Dimensions.Y != imageGain.Dims.Y)
                    throw new Exception("Gain reference dimensions do not match image.");

            bool IsTiff = header.GetType() == typeof(HeaderTiff);

            int NThreads = IsTiff ? 6 : 2;
            int GPUThreads = 2;

            int CurrentDevice = GPU.GetDevice();

            if (RawLayers == null || RawLayers.Length != NThreads || RawLayers[0].Length != header.Dimensions.ElementsSlice())
                RawLayers = Helper.ArrayOfFunction(i => new float[header.Dimensions.ElementsSlice()], NThreads);

            Image[] GPULayers = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, header.Dimensions.Slice()), GPUThreads);
            Image[] GPULayers2 = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, header.Dimensions.Slice()), GPUThreads);

            if (scaleFactor == 1M)
            {
                stack = new Image(header.Dimensions);
                float[][] OriginalStackData = stack.GetHost(Intent.Write);

                object[] Locks = Helper.ArrayOfFunction(i => new object(), GPUThreads);

                Helper.ForCPU(0, header.Dimensions.Z, NThreads, threadID => GPU.SetDevice(DeviceID), (z, threadID) =>
                {
                    if (IsTiff)
                        TiffNative.ReadTIFFPatient(50, 500, path, z, true, RawLayers[threadID]);
                    else
                        IOHelper.ReadMapFloatPatient(50, 500,
                                                     path,
                                                     HeaderlessDims,
                                                     (int)HeaderlessOffset,
                                                     ImageFormatsHelper.StringToType(HeaderlessType),
                                                     z,
                                                     null,
                                                     new[] { RawLayers[threadID] });

                    int GPUThreadID = threadID % GPUThreads;

                    lock (Locks[GPUThreadID])
                    {
                        GPU.CopyHostToDevice(RawLayers[threadID], GPULayers[GPUThreadID].GetDevice(Intent.Write), RawLayers[threadID].Length);

                        if (imageGain != null)
                            GPULayers[GPUThreadID].MultiplySlices(imageGain);

                        GPU.Xray(GPULayers[GPUThreadID].GetDevice(Intent.Read),
                                 GPULayers2[GPUThreadID].GetDevice(Intent.Write),
                                 20f,
                                 new int2(header.Dimensions),
                                 1);

                        GPU.CopyDeviceToHost(GPULayers2[GPUThreadID].GetDevice(Intent.Read),
                                             OriginalStackData[z],
                                             header.Dimensions.ElementsSlice());
                    }

                }, null);
            }
            else
            {
                int3 ScaledDims = new int3((int)Math.Round(header.Dimensions.X * scaleFactor) / 2 * 2,
                                            (int)Math.Round(header.Dimensions.Y * scaleFactor) / 2 * 2,
                                            header.Dimensions.Z);

                stack = new Image(ScaledDims);
                float[][] OriginalStackData = stack.GetHost(Intent.Write);

                int[] PlanForw = Helper.ArrayOfFunction(i => GPU.CreateFFTPlan(header.Dimensions.Slice(), 1), GPUThreads);
                int[] PlanBack = Helper.ArrayOfFunction(i => GPU.CreateIFFTPlan(ScaledDims.Slice(), 1), GPUThreads);

                Image[] GPULayersInputFT = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, header.Dimensions.Slice(), true, true), GPUThreads);
                Image[] GPULayersOutputFT = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, ScaledDims.Slice(), true, true), GPUThreads);

                Image[] GPULayersScaled = Helper.ArrayOfFunction(i => new Image(IntPtr.Zero, ScaledDims.Slice()), GPUThreads);

                object[] Locks = Helper.ArrayOfFunction(i => new object(), GPUThreads);

                Helper.ForCPU(0, ScaledDims.Z, NThreads, threadID => GPU.SetDevice(DeviceID), (z, threadID) =>
                {
                    if (IsTiff)
                        TiffNative.ReadTIFFPatient(50, 500, path, z, true, RawLayers[threadID]);
                    else
                        IOHelper.ReadMapFloatPatient(50, 500,
                                                     path,
                                                     HeaderlessDims,
                                                     (int)HeaderlessOffset,
                                                     ImageFormatsHelper.StringToType(HeaderlessType),
                                                     z,
                                                     null,
                                                     new[] { RawLayers[threadID] });

                    int GPUThreadID = threadID % GPUThreads;

                    lock (Locks[GPUThreadID])
                    {
                        GPU.CopyHostToDevice(RawLayers[threadID], GPULayers[GPUThreadID].GetDevice(Intent.Write), RawLayers[threadID].Length);

                        if (imageGain != null)
                            GPULayers[GPUThreadID].MultiplySlices(imageGain);

                        GPU.Xray(GPULayers[GPUThreadID].GetDevice(Intent.Read),
                                 GPULayers2[GPUThreadID].GetDevice(Intent.Write),
                                 20f,
                                 new int2(header.Dimensions),
                                 1);

                        GPU.Scale(GPULayers2[GPUThreadID].GetDevice(Intent.Read),
                                  GPULayersScaled[GPUThreadID].GetDevice(Intent.Write),
                                  header.Dimensions.Slice(),
                                  ScaledDims.Slice(),
                                  1,
                                  PlanForw[GPUThreadID],
                                  PlanBack[GPUThreadID],
                                  GPULayersInputFT[GPUThreadID].GetDevice(Intent.Write),
                                  GPULayersOutputFT[GPUThreadID].GetDevice(Intent.Write));

                        GPU.CopyDeviceToHost(GPULayersScaled[GPUThreadID].GetDevice(Intent.Read),
                                             OriginalStackData[z],
                                             ScaledDims.ElementsSlice());
                    }

                }, null);

                for (int i = 0; i < GPUThreads; i++)
                {
                    GPU.DestroyFFTPlan(PlanForw[i]);
                    GPU.DestroyFFTPlan(PlanBack[i]);
                    GPULayersInputFT[i].Dispose();
                    GPULayersOutputFT[i].Dispose();
                    GPULayersScaled[i].Dispose();
                }
            }

            foreach (var layer in GPULayers)
                layer.Dispose();
            foreach (var layer in GPULayers2)
                layer.Dispose();

            return stack;
        }

        #endregion
    }
}
