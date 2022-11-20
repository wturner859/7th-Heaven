/*
  This source is subject to the Microsoft Public License. See LICENSE.TXT for details.
  The original developer is Iros <irosff@outlook.com>
  Heavy rework was done by Julian Xhokaxhiu <https://julianxhokaxhiu.com>
  Additional help and support on .NET internals + low level wiring by Benjamin Moir <https://github.com/DaZombieKiller>
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Diagnostics;
using static _7thWrapperLib.Win32;

namespace _7thWrapperLib {
    public static unsafe class Wrap {
        private static Dictionary<IntPtr, LGPWrapper> _hMap = new Dictionary<IntPtr, LGPWrapper>();
        private static Dictionary<IntPtr, string> _hNames = new Dictionary<IntPtr, string>();
        private static Dictionary<IntPtr, VStreamFile> _streamFiles = new Dictionary<IntPtr, VStreamFile>();
        //private static Dictionary<IntPtr, string> _saveFiles = new Dictionary<IntPtr, string>();
        private static Dictionary<IntPtr, VArchiveData> _varchives = new Dictionary<IntPtr, VArchiveData>();
        private static RuntimeProfile _profile;

        private static void MonitorThread(object rpo)
        {
            RuntimeProfile rp = (RuntimeProfile)rpo;
            var accessors = rp.MonitorVars
                .Select(t => new { Name = t.Item1, Data = t.Item2.Split(':') })
                .Select(a => new { Type = (VarType)Enum.Parse(typeof(VarType), a.Data[0]), Addr = new IntPtr(RuntimeVar.Parse(a.Data[1])), Name = a.Name, Mask = a.Data.Length < 3 ? -1 : (int)RuntimeVar.Parse(a.Data[2]) })
                .Where(a => (int)a.Type <= 2)
                .ToList();
            int[] values = accessors.Select(_ => 247834893).ToArray();

            do
            {
                DebugLogger.WriteLine("MONITOR:");
                for (int i = 0; i < accessors.Count; i++)
                {
                    int value;
                    switch (accessors[i].Type)
                    {
                        case VarType.Int:
                            value = System.Runtime.InteropServices.Marshal.ReadInt32(accessors[i].Addr);
                            break;
                        case VarType.Short:
                            value = System.Runtime.InteropServices.Marshal.ReadInt16(accessors[i].Addr);
                            break;
                        case VarType.Byte:
                            value = System.Runtime.InteropServices.Marshal.ReadByte(accessors[i].Addr);
                            break;
                        default:
                            continue;
                    }
                    value = value & accessors[i].Mask;
                    if (value != values[i])
                    {
                        values[i] = value;
                        DebugLogger.WriteLine($"  {accessors[i].Name} = {value}");
                    }
                }
            } while (true);
        }

        public unsafe static void Run(Process currentProcess, string profileFile = ".7thWrapperProfile")
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US", false);
            try {
                using (var fs = new FileStream(profileFile, FileMode.Open))
                {
                    _profile = Iros._7th.Util.DeserializeBinary<RuntimeProfile>(fs);
                }

                File.Delete(profileFile);

                if (!String.IsNullOrWhiteSpace(_profile.LogFile)) {
                    try {
                        try { File.Delete(_profile.LogFile); } catch { } // ensure old log is deleted since new run

                        DebugLogger.Init(_profile.LogFile);
                        DebugLogger.IsDetailedLogging = _profile.Options.HasFlag(RuntimeOptions.DetailedLog);

                        DebugLogger.WriteLine("Logging debug output to " + _profile.LogFile);
                    } catch (Exception ex) {
                        DebugLogger.WriteLine("Failed to log debug output: " + ex.ToString());
                    }
                }

                DebugLogger.WriteLine($"Wrap run... PName: {currentProcess.ProcessName} PID: {currentProcess.Id} Path: {_profile.ModPath} Capture: {String.Join(", ", _profile.MonitorPaths)}");
                for (int i = _profile.MonitorPaths.Count - 1; i >= 0; i--) {
                    if (!_profile.MonitorPaths[i].EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()))
                        _profile.MonitorPaths[i] += System.IO.Path.DirectorySeparatorChar;
                    if (String.IsNullOrWhiteSpace(_profile.MonitorPaths[i])) _profile.MonitorPaths.RemoveAt(i);
                }

                foreach (var item in _profile.Mods) {
                    DebugLogger.WriteLine($"  Mod: {item.BaseFolder} has {item.Conditionals.Count} conditionals");
                    DebugLogger.WriteLine("     Additional paths: " + String.Join(", ", item.ExtraFolders));
                    item.Startup();
                }

                if (_profile.MonitorVars != null)
                    new System.Threading.Thread(MonitorThread) { IsBackground = true }.Start(_profile);

                foreach (string LL in _profile.Mods.SelectMany(m => m.GetLoadLibraries())) {
                    DebugLogger.WriteLine($"Loading library DLL {LL}");
                    NativeLibrary.Load(LL);
                }

                foreach (var mod in _profile.Mods) {
                    foreach (string LA in mod.GetLoadAssemblies()) {
                        DebugLogger.WriteLine($"Loading assembly DLL {LA}");
                        var asm = System.Reflection.Assembly.LoadFrom(LA);
                        try {
                            string path = mod.BaseFolder;
                            asm.GetType("_7thHeaven.Main")
                                .GetMethod("Init", new[] { typeof(RuntimeMod) })
                                .Invoke(null, new object[] { mod });
                        } catch { }
                    }
                }

                foreach (var mod in _profile.Mods.AsEnumerable().Reverse()) {
                    foreach (string file in mod.GetPathOverrideNames("hext")) {
                        foreach (var of in mod.GetOverrides("hext\\" + file)) {
                            System.IO.Stream s;
                            if (of.Archive == null) {
                                s = new System.IO.FileStream(of.File, FileMode.Open, FileAccess.Read);
                            } else {
                                s = of.Archive.GetData(of.File);
                            }
                            DebugLogger.WriteLine($"Applying hext patch {file} from mod {mod.BaseFolder}");
                            try {
                                HexPatch.Apply(s);
                            } catch (Exception ex) {
                                DebugLogger.WriteLine("Error applying patch: " + ex.Message);
                            }
                        }
                    }
                }
            } catch (Exception e) {
                DebugLogger.WriteLine(e.ToString());
                return;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        [UnmanagedCallersOnly]
        static int HCloseHandle(void* hObject)
        {
            int ret = 0;

            try
            {
                VArchiveData va;
                IntPtr _hObject = new IntPtr(hObject);

                if (_varchives.TryGetValue(_hObject, out va))
                {
                    _varchives.Remove(_hObject);
                    DebugLogger.WriteLine($"Closing dummy handle {_hObject}");
                }

                if (_streamFiles.ContainsKey(_hObject))
                    _streamFiles.Remove(_hObject);

                //if (_saveFiles.ContainsKey(_hObject))
                //    _saveFiles.Remove(_hObject);

                //ret = s_Trampolines.CloseHandle(hObject);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return ret;
        }

        [UnmanagedCallersOnly]
        static uint HGetFileType(void* hFile)
        {            
            uint ret = 0;

            try
            {
                IntPtr _hFile = new IntPtr(hFile);
                DebugLogger.DetailedWriteLine($"GetFileType on {_hFile}");
                VArchiveData va;
                if (_varchives.TryGetValue(_hFile, out va))
                {
                    //DebugLogger.WriteLine(" ---faking dummy file");
                    ret = 1;
                }
                else
                {
                    //ret = s_Trampolines.GetFileType(hFile);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return ret;
        }

        [UnmanagedCallersOnly]
        static uint HSetFilePointer(void* handle, long lDistanceTomove, long* lpDistanceToMoveHigh, uint dwMoveMethod)
        {
            //DebugLogger.WriteLine("SetFilePointer on {0} to {1} by {2}", handle, lDistanceTomove, dwMoveMethod);
            uint ret = 0;

            try
            {
                VArchiveData va;
                VStreamFile vsf;
                long offset = lDistanceTomove;
                if (lpDistanceToMoveHigh != null)
                    offset |= ((long)lpDistanceToMoveHigh << 32);

                IntPtr _handle = new IntPtr(handle);
                if (_varchives.TryGetValue(_handle, out va))
                {
                    ret = (uint)va.SetFilePointer(offset, (Win32.EMoveMethod)dwMoveMethod);
                }
                else if (_streamFiles.TryGetValue(_handle, out vsf))
                {
                    ret = (uint)_streamFiles[_handle].SetPosition(offset, (Win32.EMoveMethod)dwMoveMethod);
                }
                else
                {
                    //ret = s_Trampolines.SetFilePointer(handle, lDistanceTomove, lpDistanceToMoveHigh, dwMoveMethod);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
            
            return ret;
        }

        //[UnmanagedCallersOnly]
        //static unsafe int HWriteFile(void* hFile, void* lpBuffer, uint nNumberOfBytesToWrite, uint* lpNumberOfBytesWritten, void* lpOverlapped)
        //{
        //    int result = 0;
        //    *lpNumberOfBytesWritten = 0;
        //    try
        //    {
        //        result = s_Trampolines.WriteFile(hFile, lpBuffer, nNumberOfBytesToWrite, lpNumberOfBytesWritten, lpOverlapped);
        //        //DebugLogger.WriteLine(String.Format("Write {0} bytes on {1}", lpNumberOfBytesWritten, hFile.ToInt32()));

        //        IntPtr _hFile = new IntPtr(hFile);
        //        if (_saveFiles.ContainsKey(_hFile))
        //        {
        //            uint offset = s_Trampolines.SetFilePointer(hFile, 0, null, (uint)Win32.EMoveMethod.Current);
        //            //DebugLogger.WriteLine(String.Format("Write {0} bytes to {1} at offset {2}", lpNumberOfBytesWritten, _saveFiles[hFile], offset));
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine(ex.ToString());
        //    }

        //    return result;
        //}

        [UnmanagedCallersOnly]
        static int HReadFile(void* handle, void* bytes, uint numBytesToRead, uint* numBytesRead, void* overlapped)
        {
            int ret = 0;
            try
            {
                VArchiveData va;
                VStreamFile vsf;
                LGPWrapper lgp;
                IntPtr _handle = new IntPtr(handle);
                IntPtr _bytes = new IntPtr(bytes);

                if (_varchives.TryGetValue(_handle, out va))
                    ret = va.ReadFile(_bytes, numBytesToRead, ref *numBytesRead);
                else if (_streamFiles.TryGetValue(_handle, out vsf))
                    ret = vsf.Read(_bytes, *numBytesRead, ref *numBytesRead);
                else if (_hMap.TryGetValue(_handle, out lgp))
                {
                    //DebugLogger.WriteLine("Hooked ReadFile on {0} for {1} bytes", handle.ToInt32(), numBytesToRead);
                    //if (overlapped != IntPtr.Zero) DebugLogger.WriteLine("(is overlapped)");

                    int pos = SetFilePointer(_handle, 0, IntPtr.Zero, EMoveMethod.Current);
                    //DebugLogger.WriteLine("Hooked ReadFile on {0} for {1} bytes at {2}", handle.ToInt32(), numBytesToRead, pos);
                    lgp.VFile.Read((uint)pos, numBytesToRead, _bytes, ref *numBytesRead);
                    //DebugLogger.WriteLine("--{0} bytes read", numBytesRead);
                    SetFilePointer(_handle, (int)(pos + numBytesRead), IntPtr.Zero, EMoveMethod.Begin);
                    lgp.Ping();
                    ret = -1;
                }
                //else
                //    ret = s_Trampolines.ReadFile(handle, bytes, numBytesToRead, numBytesRead, overlapped);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return ret;
        }

        static IntPtr CreateVA(OverrideFile of) {
            IntPtr dummy = IntPtr.Zero;

            try
            {
                VArchiveData va = new VArchiveData(of.Archive.GetBytes(of.File));
                dummy = of.Archive.GetDummyHandle();
                DebugLogger.WriteLine($"Creating dummy file handle {dummy} to access {of.Archive}{of.File}");
                _varchives[dummy] = va;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return dummy;
        }

        // ushort*, uint, uint, void*, uint, uint, void*, void*
        [UnmanagedCallersOnly]
        static void* HCreateFileW(ushort* lpFileName, uint dwDesiredAccess, uint dwShareMode, void*lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, void* hTemplateFile)
        {
            void* ret = null;
            try
            {
                string _lpFileName = new string((char*)lpFileName);

                // Usually this check should be enough...
                bool isFF7GameFile = _lpFileName.StartsWith(_profile.FF7Path, StringComparison.InvariantCultureIgnoreCase);
                // ...but if it fails, last resort is to check if the file exists in the game directory
                if (!isFF7GameFile && !_lpFileName.StartsWith("\\", StringComparison.InvariantCultureIgnoreCase) && !Path.IsPathRooted(_lpFileName))
                {
                    isFF7GameFile = _profile.gameFiles.Any(s => s.EndsWith(_lpFileName, StringComparison.InvariantCultureIgnoreCase));
                }

                // If a game file is found, process with replacing its content with relative mod file
                if (isFF7GameFile)
                {
                    _lpFileName = _lpFileName.Replace("\\/", "\\").Replace("/", "\\").Replace("\\\\", "\\");
                    DebugLogger.DetailedWriteLine($"CreateFileW for {_lpFileName}...");
                    if (_lpFileName.IndexOf('\\') < 0)
                    {
                        //DebugLogger.WriteLine("No path: curdir is {0}", System.IO.Directory.GetCurrentDirectory(), 0);
                        _lpFileName = Path.Combine(Directory.GetCurrentDirectory(), _lpFileName);
                    }

                    foreach (string path in _profile.MonitorPaths)
                    {
                        if (_lpFileName.StartsWith(path, StringComparison.InvariantCultureIgnoreCase))
                        {
                            string match = _lpFileName.Substring(path.Length);
                            OverrideFile mapped = LGPWrapper.MapFile(match, _profile);

                            //DebugLogger.WriteLine($"Attempting match '{match}' for {_lpFileName}...");

                            if (mapped == null)
                            {
                                // Attempt a second round, this time relaxing the path match replacing only the game folder path.
                                match = _lpFileName.Substring(_profile.FF7Path.Length + 1);
                                mapped = LGPWrapper.MapFile(match, _profile);

                                //DebugLogger.WriteLine($"Attempting match '{match}' for {_lpFileName}...");
                            }

                            if (mapped != null)
                            {
                                DebugLogger.WriteLine($"Remapping {_lpFileName} to {mapped.File} [ Matched: '{match}' ]");

                                if (mapped.Archive == null)
                                    _lpFileName = mapped.File;
                                else
                                {
                                    ret = CreateVA(mapped).ToPointer();
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                    DebugLogger.DetailedWriteLine($"Skipped file {_lpFileName}");

                //if (ret == null)
                //    ret = s_Trampolines.CreateFileW((ushort*)Marshal.StringToHGlobalAuto(_lpFileName).ToPointer(), dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);

                //DebugLogger.WriteLine("Hooked CreateFileW for {0} under {1}", _lpFileName, handle.ToInt32());

                //if (isFF7GameFile && ret != null)
                //{
                //    IntPtr _ret = new IntPtr(ret);

                //    if (System.IO.Path.GetExtension(_lpFileName).Equals(".ff7", StringComparison.InvariantCultureIgnoreCase))
                //    {
                //        _saveFiles.Add(_ret, _lpFileName);
                //    }

                //    DebugLogger.DetailedWriteLine($"CreateFileW: {_lpFileName} -> {_ret}");
                //}
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return ret;
        }

        [UnmanagedCallersOnly]
        static void* HFindFirstFileW(ushort* lpFileName, void* lpFindFileData) {
            void* ret = null;

            try
            {
                DebugLogger.WriteLine("FindFirstFile for " + new string((char*)lpFileName));
                //ret = s_Trampolines.FindFirstFileW(lpFileName, lpFindFileData);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return ret;
        }

        //[UnmanagedCallersOnly]
        //private static bool HCreateProcessW(string lpApplicationName,
        //    string lpCommandLine, ref SECURITY_ATTRIBUTES lpProcessAttributes,
        //    ref SECURITY_ATTRIBUTES lpThreadAttributes, bool bInheritHandles,
        //    uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        //    [In] ref STARTUPINFO lpStartupInfo,
        //    out PROCESS_INFORMATION lpProcessInformation)
        //{

        //    DebugLogger.WriteLine($"CreateProcessW for {lpApplicationName}, {lpCommandLine}");
        //    string exe = lpApplicationName;
        //    if (String.IsNullOrWhiteSpace(exe)) exe = lpCommandLine;
        //    exe = exe.Replace('/', '\\');

        //    bool ret;
        //    if (System.IO.Path.GetFileName(exe).IndexOf("FF7", StringComparison.InvariantCultureIgnoreCase) >= 0)
        //    {
        //        if (System.IO.File.Exists(exe + ".exe")) exe += ".exe";
        //        //int pid;
        //        try
        //        {
        //            string lib = System.Reflection.Assembly.GetExecutingAssembly().Location;
        //            DebugLogger.WriteLine("--Injecting into " + exe + " with library " + lib);

        //            ret = CreateProcessW(lpApplicationName, lpCommandLine, ref lpProcessAttributes, ref lpThreadAttributes, bInheritHandles, dwCreationFlags, lpEnvironment, lpCurrentDirectory, ref lpStartupInfo, out lpProcessInformation);
        //        }
        //        catch (Exception ex)
        //        {
        //            DebugLogger.WriteLine(ex.ToString());
        //            throw;
        //        }
        //    }
        //    else
        //        ret = CreateProcessW(lpApplicationName, lpCommandLine, ref lpProcessAttributes, ref lpThreadAttributes, bInheritHandles, dwCreationFlags, lpEnvironment, lpCurrentDirectory, ref lpStartupInfo, out lpProcessInformation);

        //    return ret;
        //}

        [UnmanagedCallersOnly]
        static int HGetFileInformationByHandle(void* hFile, Win32.BY_HANDLE_FILE_INFORMATION* lpFileInformation) {
            int result = 0;
            try
            {
                //result = s_Trampolines.GetFileInformationByHandle(hFile, lpFileInformation);
                VArchiveData va;
                IntPtr _hFile = new IntPtr(hFile);
                if (result > 0 && _varchives.TryGetValue(_hFile, out va))
                {
                    DebugLogger.DetailedWriteLine($"Overriding GetFileInformationByHandle for dummy file {_hFile}");
                    lpFileInformation->FileSizeHigh = (uint)(va.Size >> 32);
                    lpFileInformation->FileSizeLow = (uint)(va.Size & 0xffffffff);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return result;
        }

        [UnmanagedCallersOnly]
        static int HDuplicateHandle(void* hSourceProcessHandle, void* hSourceHandle, void* hTargetProcessHandle, void** lpTargetHandle, uint dwDesiredAccess, int bInheritHandle, uint dwOptions)
        {
            // DebugLogger.DetailedWriteLine("DuplicateHandle on {0}", hSourceHandle);

            int result = 0;
            try
            {
                IntPtr _hSourceHandle = new IntPtr(hSourceHandle);
                IntPtr _lpTargetHandle = new IntPtr(lpTargetHandle);
               //result = s_Trampolines.DuplicateHandle(hSourceProcessHandle, hSourceHandle, hTargetProcessHandle, lpTargetHandle, dwDesiredAccess, bInheritHandle, dwOptions);
                if (result > 0 && _varchives.ContainsKey(_hSourceHandle))
                {
                    _varchives[_lpTargetHandle] = _varchives[_hSourceHandle];
                    DebugLogger.DetailedWriteLine($"Duplicating dummy handle {_hSourceHandle} to {_lpTargetHandle}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return result;
        }

        [UnmanagedCallersOnly]
        static uint HGetFileSize(void* hFile, uint* lpFileSizeHigh)
        {
            VArchiveData va;

            uint ret = 0;
            try
            {
                IntPtr _hFile = new IntPtr(hFile);
                IntPtr _lpFileSizeHigh = new IntPtr(lpFileSizeHigh);
                if (_varchives.TryGetValue(_hFile, out va))
                    ret = va.GetFileSize(_lpFileSizeHigh);
                //else
                //    ret = s_Trampolines.GetFileSize(hFile, lpFileSizeHigh);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return ret;
        }

        [UnmanagedCallersOnly]
        static uint HGetFileSizeEx(void* hFile, uint* lpFileSize)
        {
            uint ret = 0;
            try
            {
                VArchiveData va;
                IntPtr _hFile = new IntPtr(hFile);
                if (_varchives.TryGetValue(_hFile, out va))
                {
                    DebugLogger.WriteLine($"GetFileSizeEx on dummy handle {_hFile}");
                    *lpFileSize = (uint)va.Size;
                    ret = 1;
                }
                //else
                //    ret = s_Trampolines.GetFileSizeEx(hFile, lpFileSize);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return ret;
        }

        [UnmanagedCallersOnly]
        static int HSetFilePointerEx(void* hFile, long liDistanceToMove, long* lpNewFilePointer, uint dwMoveMethod)
        {
            VArchiveData va;

            int ret = 0;
            try
            {
                IntPtr _hFile = new IntPtr(hFile);
                IntPtr _lpNewFilePointer = new IntPtr(lpNewFilePointer);

                if (_varchives.TryGetValue(_hFile, out va))
                    ret = va.SetFilePointerEx(_hFile, liDistanceToMove, _lpNewFilePointer, (uint)dwMoveMethod);
                //else
                //    ret = s_Trampolines.SetFilePointerEx(hFile, liDistanceToMove, lpNewFilePointer, dwMoveMethod);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }

            return ret;
        }
    }
}
