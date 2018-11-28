﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using CoreHook.BinaryInjection.Loader;
using CoreHook.BinaryInjection.Loader.Serializer;
using CoreHook.BinaryInjection.Host;
using CoreHook.CoreLoad.Data;
using CoreHook.IPC.Platform;
using CoreHook.Memory;
using CoreHook.Memory.Processes;
using CoreHook.BinaryInjection.ProcessUtils;
using static CoreHook.BinaryInjection.ProcessUtils.ProcessHelper;

namespace CoreHook.BinaryInjection.RemoteInjection
{
    public static class RemoteInjector
    {
        /// <summary>
        /// The .NET Assembly class that loads the .NET hooking library, resolves any references, and executes
        /// the hooking library IEntryPoint.Run method.
        /// </summary>
        private static readonly IAssemblyDelegate CoreHookLoaderDelegate =
                new AssemblyDelegate(
                assemblyName: "CoreHook.CoreLoad",
                typeName: "Loader",
                methodName: "Load");

        /// <summary>
        /// Retrieve the class used to load binary modules in a process.
        /// </summary>
        /// <param name="process">The target process.</param>
        /// <returns>The class that handles binary handling.</returns>
        private static IBinaryLoader GetBinaryLoader(Process process)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var managedProcess = new ManagedProcess(process);
                return new BinaryLoader(
                    new ProcessManager(managedProcess,
                    new MemoryManager(managedProcess)));
            }
            throw new PlatformNotSupportedException("Binary injection");
        }

        /// <summary>
        /// Retrieve system information such as string path encoding and max path length.
        /// </summary>
        /// <returns>Configuration class with system information.</returns>
        private static IBinaryLoaderConfig GetBinaryLoaderConfig() => new BinaryLoaderConfig();

        /// <summary>
        /// Get the name of the function that starts CoreCLR in a target process
        /// </summary>
        /// <returns>The name of the library function used to start CoreCLR.</returns>
        private static string GetCoreCLRStartFunctionName() => BinaryLoaderHostConfig.CoreCLRStartFunction;

        /// <summary>
        /// Get the name of a function that executes a single function inside
        /// a .NET library loaded in a process, referenced by class name
        /// and function name.
        /// </summary>
        /// <returns>The name of the library function used to execute the .NET
        /// Bootstrapping module, CoreLoad.
        /// </returns>
        private static string GetCoreCLRExecuteManagedFunctionName() => BinaryLoaderHostConfig.CoreCLRExecuteManagedFunction;

        /// <summary>
        /// Create a process, inject the .NET Core runtime into it and load a .NET assembly.
        /// </summary>
        /// <param name="processConfig"></param>
        /// <param name="config32">Native modules required for starting CoreCLR in 32-bit applications.</param>
        /// <param name="config64">Native modules required for starting CoreCLR in 64-bit applications.</param>
        /// <param name="remoteInjectorConfig">Configuration settings for starting CoreCLR and executing .NET assemblies.</param>
        /// <param name="pipePlatform">Class for creating pipes for communication with the target process.</param>
        /// <param name="createdProcessId">Process ID of the newly created process.</param>
        /// <param name="passThruArguments">Arguments passed to the .NET hooking library in the target process.</param>
        public static void CreateAndInject(
            ProcessCreationConfig processConfig,
            CoreHookNativeConfig config32,
            CoreHookNativeConfig config64,
            RemoteInjectorConfig remoteInjectorConfig,
            IPipePlatform pipePlatform,
            out int createdProcessId,
            params object[] passThruArguments
            )
        {
            var process = Process.Start(processConfig.ExecutablePath);
            if (process == null)
            {
                throw new InvalidOperationException(
                    $"Failed to start the executable at {processConfig.ExecutablePath}");
            }

            var config = process.Is64Bit() ? config64 : config32;
            
            remoteInjectorConfig.HostLibrary = config.HostLibrary;
            remoteInjectorConfig.CoreCLRPath = config.CoreCLRPath;
            remoteInjectorConfig.CoreCLRLibrariesPath = config.CoreCLRLibrariesPath;
            remoteInjectorConfig.DetourLibrary = config.DetourLibrary;

            InjectEx(
                GetCurrentProcessId(),
                process.Id,
                remoteInjectorConfig,
                pipePlatform,
                passThruArguments);

            createdProcessId = process.Id;
        }

        /// <summary>
        /// Start CoreCLR and execute a .NET assembly in a target process.
        /// </summary>
        /// <param name="targetProcessId">The process ID of the process to inject the .NET assembly into.</param>
        /// <param name="remoteInjectorConfig">Configuration settings for starting CoreCLR and executing .NET assemblies.</param>
        /// <param name="pipePlatform">Class for creating pipes for communication with the target process.</param>
        /// <param name="passThruArguments">Arguments passed to the .NET hooking library in the target process.</param>
        public static void Inject(
            int targetProcessId,
            RemoteInjectorConfig remoteInjectorConfig,
            IPipePlatform pipePlatform,
            params object[] passThruArguments)
        {
            InjectEx(
                GetCurrentProcessId(),
                targetProcessId,
                remoteInjectorConfig,
                pipePlatform,
                passThruArguments);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="localProcessId">The process ID of the local process that is injecting into another remote process.</param>
        /// <param name="formatter">Serializer for the user arguments passed to the plugin.</param>
        /// <param name="passThruArguments">The arguments passed to the plugin during initialization.</param>
        /// <returns></returns>
        private static ManagedRemoteInfo CreateRemoteInfo(int localProcessId, IUserDataFormatter formatter, params object[] passThruArguments)
        {
            var remoteInfo = new ManagedRemoteInfo { HostPID = localProcessId };

            //var format = new BinaryFormatter();
            var arguments = new List<object>();
            if (passThruArguments != null)
            {
                foreach (var arg in passThruArguments)
                {
                    using (var ms = new MemoryStream())
                    {
                        formatter.Serialize(ms, arg);
                        arguments.Add(ms.ToArray());
                    }
                }
            }
            remoteInfo.UserParams = arguments.ToArray();

            return remoteInfo;
        }

        /// <summary>
        /// Start CoreCLR and execute a .NET assembly in a target process.
        /// </summary>
        /// <param name="localProcessId">Process ID of the process communicating with the target process.</param>
        /// <param name="targetProcessId">The process ID of the process to inject the .NET assembly into.</param>
        /// <param name="remoteInjectorConfig">Configuration settings for starting CoreCLR and executing .NET assemblies.</param>
        /// <param name="pipePlatform">Class for creating pipes for communication with the target process.</param>
        /// <param name="passThruArguments">Arguments passed to the .NET hooking library in the target process.</param>
        public static void InjectEx(
            int localProcessId,
            int targetProcessId,
            RemoteInjectorConfig remoteInjectorConfig,
            IPipePlatform pipePlatform,
            params object[] passThruArguments)
        {
            if(string.IsNullOrWhiteSpace(remoteInjectorConfig.InjectionPipeName))
            {
                throw new ArgumentException("Invalid injection pipe name");
            }

            InjectionHelper.BeginInjection(targetProcessId);
            
            using (InjectionHelper.CreateServer(remoteInjectorConfig.InjectionPipeName, pipePlatform))
            {
                try
                {
                    var remoteInfoFormatter = new UserDataBinaryFormatter();
                    // Initialize the arguments passed to the CoreHook plugin
                    var remoteInfo = CreateRemoteInfo(localProcessId, remoteInfoFormatter, passThruArguments);

                    using (var passThruStream = new MemoryStream())
                    {
                        // Serialize the plugin information such as the DLL path
                        // and the plugin arguments, which are copied to the remote process
                        PrepareInjection(
                            remoteInfo,
                            remoteInfoFormatter,
                            remoteInjectorConfig.PayloadLibrary,
                            passThruStream,
                            remoteInjectorConfig.InjectionPipeName);

                        // Inject the CoreCLR hosting module into the process, start the CoreCLR
                        // and use the CoreLoad dll to resolve the dependencies of the hooking library
                        // and then call the IEntryPoint.Run method located in the hooking library
                        try
                        {
                            var process = GetProcessById(targetProcessId);
                            var length = (int)passThruStream.Length;

                            using (var binaryLoader = GetBinaryLoader(process))
                            {
                                // Load the CoreCLR hosting module in the remote process,
                                // along with the native function detour module for CoreHook
                                binaryLoader.Load(remoteInjectorConfig.HostLibrary, new[] { remoteInjectorConfig.DetourLibrary });
                                // Initialize CoreCLR in the remote process using the native CoreCLR hosting module
                                binaryLoader.ExecuteRemoteFunction(
                                    new RemoteFunctionCall
                                    {
                                        Arguments = new BinaryLoaderSerializer(GetBinaryLoaderConfig(),
                                            new BinaryLoaderArguments
                                            {
                                                Verbose = remoteInjectorConfig.VerboseLog,
                                                PayloadFileName = remoteInjectorConfig.CLRBootstrapLibrary,
                                                CoreRootPath = remoteInjectorConfig.CoreCLRPath,
                                                CoreLibrariesPath = remoteInjectorConfig.CoreCLRLibrariesPath
                                            }),
                                        FunctionName = new FunctionName { Module = remoteInjectorConfig.HostLibrary, Function = GetCoreCLRStartFunctionName() },
                                    });

                                // Execute a .NET function in the remote process now that CoreCLR is started
                                binaryLoader.ExecuteRemoteManagedFunction(
                                    new RemoteManagedFunctionCall
                                    {
                                        ManagedFunction = CoreHookLoaderDelegate,
                                        FunctionName = new FunctionName { Module = remoteInjectorConfig.HostLibrary, Function = GetCoreCLRExecuteManagedFunctionName() },
                                        Arguments = new RemoteFunctionArgumentsSerializer
                                        {
                                            Is64BitProcess = process.Is64Bit(),
                                            UserData = binaryLoader.CopyMemoryTo(passThruStream.GetBuffer(), length),
                                            UserDataSize = length
                                        }
                                    });

                                InjectionHelper.WaitForInjection(targetProcessId);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.ToString());
                        }
                    }
                }
                finally
                {
                    InjectionHelper.EndInjection(targetProcessId);
                }
            }
        }

        /// <summary>
        /// Create the config class that is passed to the CLR bootstrap library to be loaded.
        /// The <paramref name="remoteInfo"/> holds information such as what hooking module to load.
        /// </summary>
        /// <param name="remoteInfo">The configuration that is serialized and passed to CoreLoad.</param>
        /// <param name="serializer">Serializes the <paramref name="remoteInfo"/> data.</param>
        /// <param name="pluginPath">The plugin to be loaded and executed in the target process.</param>
        /// <param name="argumentsStream">The stream that holds the the serialized <paramref name="remoteInfo"/> class.</param>
        /// <param name="injectionPipeName">The pipe name used for notifying the host process that the hook plugin has been loaded in the target process.</param>
        private static void PrepareInjection(
            ManagedRemoteInfo remoteInfo,
            IUserDataFormatter serializer,
            string pluginPath,
            MemoryStream argumentsStream,
            string injectionPipeName)
        {
            if (string.IsNullOrWhiteSpace(pluginPath))
            {
                throw new ArgumentException("The injection library was not valid");
            }

            if (File.Exists(pluginPath))
            {
                pluginPath = Path.GetFullPath(pluginPath);
            }

            remoteInfo.UserLibrary = pluginPath;

            if (File.Exists(remoteInfo.UserLibrary))
            {
                remoteInfo.UserLibraryName = AssemblyName.GetAssemblyName(remoteInfo.UserLibrary).FullName;
            }
            else
            {
                throw new FileNotFoundException($"The given assembly could not be found: '{remoteInfo.UserLibrary}'", remoteInfo.UserLibrary);
            }

            remoteInfo.ChannelName = injectionPipeName;

            serializer.Serialize(argumentsStream, remoteInfo);
        }
    }
}