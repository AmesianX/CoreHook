﻿using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CoreHook.BinaryInjection.ProcessUtils;
using CoreHook.BinaryInjection.RemoteInjection;

namespace CoreHook.Examples.Common
{
    public class ModulesPathHelper
    {
        /// <summary>
        /// The name of the .NET Core hosting module for 64-bit processes.
        /// </summary>
        private const string CoreHostModule64 = "coreload64.dll";
        /// <summary>
        /// The name of the .NET Core hosting module for 32-bit processes.
        /// </summary>
        private const string CoreHostModule32 = "coreload32.dll";
        /// <summary>
        /// The name of the native detour module for 64-bit processes.
        /// </summary>
        private const string CoreHookingModule64 = "corehook64.dll";
        /// <summary>
        /// The name of the native detour module for 32-bit processes.
        /// </summary>
        private const string CoreHookingModule32 = "corehook32.dll";
        /// <summary>
        /// Module that loads and executes the IEntryPoint.Run method of our hook dll.
        /// It also resolves any dependencies for the CoreHook plugin.
        /// </summary>
        private const string CoreLoadModule = "CoreHook.CoreLoad.dll";

        // For more information o on these environment variables, see: 
        // https://github.com/dotnet/coreclr/blob/master/Documentation/workflow/UsingCoreRun.md

        public static string GetCoreRootPath(bool is64BitProcess)
        {
            return !ProcessHelper.IsArchitectureArm() ?
             (
                is64BitProcess ?
                 Environment.GetEnvironmentVariable("CORE_ROOT_64") :
                 Environment.GetEnvironmentVariable("CORE_ROOT_32")
             )
             : Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        private static void HandleFileNotFound(string path)
        {
            Console.WriteLine($"Cannot find file {Path.GetFileName(path)}");
        }

        /// <summary>
        /// Get the path of the .NET Assembly that is first loaded by the host 
        /// and initializes the dependencies for hooking libraries.
        /// </summary>
        /// <param name="coreLoadLibrary">The path to the .NET bootstrap library</param>
        /// <returns>Whether or not the CoreLoad module file exists.</returns>
        public static bool GetCoreLoadModulePath(out string coreLoadLibrary)
        {
            coreLoadLibrary = null;

            string currentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (string.IsNullOrWhiteSpace(currentDir))
            {
                return false;
            }

            var coreLoadPath = Path.Combine(currentDir, CoreLoadModule);

            if (!File.Exists(coreLoadPath))
            {
                HandleFileNotFound(coreLoadPath);
                return false;
            }

            coreLoadLibrary = coreLoadPath;

            return true;
        }

        /// <summary>
        /// Determine if the current application is a self-contained application.
        /// </summary>
        /// <param name="applicationBase">The application base directory.</param>
        /// <returns>True if the coreclr module exists in the application base.</returns>
        private static bool IsPublishedApplication(string applicationBase)
        {
            return File.Exists(
                Path.Combine(applicationBase, "coreclr.dll"));
        }

        /// <summary>
        /// Determine if the application has a local CoreCLR runtime configuration file.
        /// </summary>
        /// <param name="applicationBase">The application base directory.</param>
        /// <returns>True if there the directory contains a runtime configuration file.</returns>
        private static bool HasLocalRuntimeConfiguration(string applicationBase)
        {
            // The configuration file should be named 'CoreHook.CoreLoad.runtimeconfig.json'
            return File.Exists(Path.Combine(applicationBase,
                CoreLoadModule.Replace("dll", "runtimeconfig.json")));
        }

        /// <summary>
        /// Determine if the path CoreCLR runtime configuration file.
        /// </summary>
        /// <param name="configurationBase">Directory that should contain a CoreCLR runtime configuration file.</param>
        /// <returns>True if there the directory contains a runtime configuration file.</returns>
        private static bool DoesDirectoryContainRuntimeConfiguration(string configurationBase)
        {
            // The configuration file should be named 'dotnet.runtimeconfig.json'
            return File.Exists(Path.Combine(configurationBase, "dotnet.runtimeconfig.json"));
        }

        /// <summary>
        /// Get the directory path of the .NET Core runtime configuration file.
        /// </summary>
        /// <param name="is64BitProcess">Value to determine which native modules path to look for.</param>
        /// <param name="coreRootPath">Path to the directory containing the CoreCLR runtime configuration.</param>
        /// <returns>Whether the CoreCLR path was found or not.</returns>
        public static bool GetCoreClrRootPath(
            bool is64BitProcess,
            out string coreRootPath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var applicationBase = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrWhiteSpace(applicationBase))
                {
                    // Check if we are using a published application or a local
                    // runtime configuration file, in which case we don't need
                    // the paths from the environment variables.
                    if (IsPublishedApplication(applicationBase)
                        || HasLocalRuntimeConfiguration(applicationBase))
                    {
                        // Set the directory for finding dependencies to the application base directory.
                        coreRootPath = applicationBase;
                        return true;
                    }
                }
            }

            // Path to the directory containing the CoreCLR runtime configuration file.
            coreRootPath = GetCoreRootPath(is64BitProcess);

            if (string.IsNullOrWhiteSpace(coreRootPath) || !DoesDirectoryContainRuntimeConfiguration(coreRootPath))
            {
                Console.WriteLine(is64BitProcess
                    ? "CoreCLR configuration was not found for 64-bit processes."
                    : "CoreCLR configuration was not found for 32-bit processes.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Retrieve the required paths for initializing the CoreCLR and executing .NET assemblies in an unmanaged process.
        /// </summary>
        /// <param name="is64BitProcess">Flag for determining which native modules to load into the target process</param>
        /// <param name="nativeModulesConfig">Configuration class containing paths to the native modules used by CoreHook.</param>
        /// <returns>Returns whether all required paths and modules have been found.</returns>
        public static bool GetCoreLoadPaths(
            bool is64BitProcess,
            out NativeModulesConfiguration nativeModulesConfig)
        {
            nativeModulesConfig = null;

            string currentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!string.IsNullOrWhiteSpace(currentDir) && GetCoreClrRootPath(
                is64BitProcess,
                out string coreRootPath))
            {
                // Module that initializes the .NET Core runtime and executes .NET assemblies
                var coreRunPath = Path.Combine(
                    currentDir,
                    is64BitProcess ? CoreHostModule64 : CoreHostModule32);
                if (!File.Exists(coreRunPath))
                {
                    HandleFileNotFound(coreRunPath);
                    return false;
                }

                var corehookPath = Path.Combine(
                    currentDir,
                    is64BitProcess ? CoreHookingModule64 : CoreHookingModule32);
                if (!File.Exists(corehookPath))
                {
                    HandleFileNotFound(corehookPath);
                    return false;
                }

                nativeModulesConfig = new NativeModulesConfiguration
                {
                    ClrRootPath = coreRootPath,
                    HostLibrary = coreRunPath,
                    DetourLibrary = corehookPath
                };

                return true;
            }
            return false;
        }
    }
}
