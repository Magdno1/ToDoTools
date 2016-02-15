﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using CRH.Framework.Disk;
using CRH.Framework.Disk.DataTrack;
using CRH.Framework.Disk.AudioTrack;
using ToDoTools.sources;

namespace ToDoTools
{
    class ToDTMain
    {
        static Stopwatch sw_watch;
        static private cGlobal global;

        static void Main(string[] args)
        {
            ConsoleTraceListener ctl_trace = new ConsoleTraceListener();
            Trace.Listeners.Add(ctl_trace);

            global = cGlobal.INSTANCE;
            global.ts_TypeTrace.Level = TraceLevel.Error;
            sw_watch = new Stopwatch();

            if (global.readArguments(args))
            {
                Trace.AutoFlush = true;

                try
                {
                    sw_watch.Start();

                    if (global.EXTRACT)
                        extractFromIso();
                    else if (global.INSERT)
                        insertToIso();
                    else if (global.UNPACK)
                        Archive.unpackFile();
                    else if (global.PACK)
                        Archive.packFile();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex.Message + Environment.NewLine, "ERROR");
                    usage();
                    return;
                }
                finally
                {
                    sw_watch.Stop();
                    Trace.WriteLine(string.Format("Terminated. Execution time : {0}", sw_watch.Elapsed));
                }
            }
        }

        public static void usage()
        {
            Trace.WriteLine("Usage : ToDoTools.exe <action> [options]");
            Trace.WriteLine("");
            Trace.WriteLine("Actions :");
            Trace.WriteLine("  extract : Extract files from image disk and unpack them (if necessary)");
            Trace.WriteLine("  insert  : Insert files to image disk after pack them (if necessary)");
            Trace.WriteLine("  unpack  : Unpack a file");
            Trace.WriteLine("  pack    : Pack files");
            Trace.WriteLine("  decomp  : Decompress a file");
            Trace.WriteLine("  comp    : Compress a file");
            Trace.WriteLine("Options :");
            Trace.WriteLine("  -i <file> : Pathname of the source file");
            Trace.WriteLine("  -o <file> : Pathname of the destination file");
            Trace.WriteLine("  -m <mode> : Compression/Archive mode (0, 1, 3 (default) / 1 (default), 2)");
            Trace.WriteLine("  -r        : Recursive mode");
            Trace.WriteLine("  -l <file> : Active log to the file");
            Trace.WriteLine("  -v        : Verbose");
        }

        /// <summary>
        /// Récupère un index présent dans le SLUS
        /// </summary>
        /// <param name="dtr_track">Track 1 de l'iso</param>
        /// <param name="i_position">Adresse de l'index dans le fichier SLUS</param>
        /// <param name="nb">Nombre de pointeurs de l'index</param>
        /// <returns></returns>
        private static List<cGlobal.st_index> readSlusIndex(DataTrackReader dtr_track, int i_position, int nb)
        {
            List<cGlobal.st_index> index = new List<cGlobal.st_index>();

            Stream st_file = dtr_track.ReadFile("/SLUS_006.26");

            using (BinaryReader br_file = new BinaryReader(st_file))
            {
                br_file.BaseStream.Seek(i_position, SeekOrigin.Begin);

                cGlobal.st_index elem;

                for (int i = 0; i < nb; i++)
                {
                    elem.id = i;
                    elem.pos = br_file.ReadUInt32();
                    elem.size = br_file.ReadUInt32();

                    index.Add(elem);
                }
            }

            return index;
        }

        /// <summary>
        /// Extract all files for image disk and unpack those are archive
        /// </summary>
        /// <returns></returns>
        public static bool extractFromIso()
        {
            try
            {
                if (!File.Exists(global.SOURCE))
                    throw new Exception(string.Format("Unknown file : {0}", global.SOURCE));

                DiskReader dr_iso = DiskReader.InitFromCue(global.SOURCE, DiskFileSystem.ISO9660);

                foreach (Track t in dr_iso.Tracks)
                {
                    if (t.IsAudio)
                    {
                        AudioTrackReader atr_track = (AudioTrackReader)t;

                        atr_track.Extract(string.Format("{0}Track{1}.bin",  global.DIR_DUMP, atr_track.TrackNumber), AudioFileContainer.WAVE);
                    }
                    else if (t.IsData)
                    {
                        DataTrackReader dtr_track = (DataTrackReader)t;

                        dtr_track.ReadVolumeDescriptors();
                        dtr_track.BuildIndex();

                        foreach (DataTrackIndexEntry entry in dtr_track.FileEntries)
                        {
                            Trace.WriteLine(string.Format("Extracting {0}...", entry.FullPath));

                            switch (Path.GetFileName(entry.FullPath))
                            {
                                case "B.DAT":
                                    dtr_track.ExtractFile(entry.FullPath, global.DIR_DUMP + entry.FullPath);
                                    List<cGlobal.st_index> index = readSlusIndex(dtr_track, 0xF3C00, 339);
                                    MemoryStream st_file = (MemoryStream)dtr_track.ReadFile(entry.FullPath);
                                    Archive.unpackBDat(st_file, index, entry.FullPath.Replace(".", "") + "/");
                                    break;

                                default:
                                    dtr_track.ExtractFile(entry.FullPath, global.DIR_DUMP + entry.FullPath);
                                    break;
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message + Environment.NewLine, "ERROR");
                return false;
            }
        }

        /// <summary>
        /// Insert all files from disk to image disk
        /// </summary>
        /// <returns></returns>
        public static bool insertToIso()
        {
            DiskReader dr_isoIn = DiskReader.InitFromCue(global.SOURCE, DiskFileSystem.ISO9660);
            DiskWriter dw_isoOut = DiskWriter.Init(global.DESTINATION, DiskFileSystem.ISO9660);

            DataTrackWriter dtw_trackOut = null;
            
            foreach (Track t in dr_isoIn.Tracks)
            {
                if (t.IsAudio)
                {
                    AudioTrackReader atr_trackIn = (AudioTrackReader)t;
                    AudioTrackWriter atw_trackOut = dw_isoOut.CreateAudioTrack();

                    atw_trackOut.Prepare();

                    Stream ms = atr_trackIn.Read();

                    atw_trackOut.Write(ms);

                    atw_trackOut.Finalize();
                }
                else if (t.IsData)
                {
                    DataTrackReader dtr_trackIn = (DataTrackReader)t;
                    dtr_trackIn.ReadVolumeDescriptors();
                    dtr_trackIn.BuildIndex();

                    dtw_trackOut = dw_isoOut.CreateDataTrack(DataTrackMode.MODE2_XA);

                    dtw_trackOut.Prepare(
                        "TOD",
                        ((int)dtr_trackIn.PrimaryVolumeDescriptor.PathTableSize / 2048) + 1,
                        (int)dtr_trackIn.PrimaryVolumeDescriptor.RootDirectoryEntry.ExtentSize / 2048
                    );

                    dtw_trackOut.CopySystemZone(dtr_trackIn);

                    dtr_trackIn.EntriesOrder = DataTrackEntriesOrder.LBA;
                    foreach (DataTrackIndexEntry entry in dtr_trackIn.Entries)
                    {
                        if (entry.IsDirectory)
                        {
                            Trace.WriteLine(string.Format("Creating directory {0}...", entry.FullPath));
                            dtw_trackOut.CreateDirectory(entry.FullPath, (int)entry.Size / 2048);
                        }
                        else if (entry.IsStream)
                        {
                            Trace.WriteLine(string.Format("Copying file {0}...", entry.FullPath));
                            dtw_trackOut.CopyStream(entry.FullPath, dtr_trackIn, entry);
                        }
                        else
                        {
                            Trace.WriteLine(string.Format("Inserting file {0}...", entry.FullPath));

                            switch (Path.GetFileName(entry.FullPath))
                            {
                                case "B.DAT":
                                    List<cGlobal.st_index> index = readSlusIndex(dtr_trackIn, 0xF3C00, 339);
                                    MemoryStream ms_orig = (MemoryStream)dtr_trackIn.ReadFile(entry.FullPath);
                                    MemoryStream ms_new = Archive.packBDat(ms_orig, index, entry.FullPath.Replace(".", "") + "/");
                                    dtw_trackOut.WriteFile(entry.FullPath, ms_new);
                                    break;

                                default:
                                    Stream ms = dtr_trackIn.ReadFile(entry.FullPath);
                                    dtw_trackOut.WriteFile(entry.FullPath, ms);
                                    break;
                            }
                        }
                    }

                    dtw_trackOut.Finalize();
                }
            }

            dtw_trackOut.FinaliseFileSystem();
            dw_isoOut.Close();
            dr_isoIn.Close();

            return true;
        }
    }
}
