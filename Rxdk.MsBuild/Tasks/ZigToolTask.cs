using Microsoft.Build.CPPTasks;
using Microsoft.Build.Framework;
//using Rxdk.Engine.Bootstrap;
//using Rxdk.Engine.Platform;
using System;
using System.Collections;
using System.IO;
using System.Text;

namespace Rxdk.MsBuild.Tasks
{
    public abstract class ZigToolTask : RxdkToolTask
    {
        public ZigToolTask()
        {
            switchOrderList = new ArrayList()
            {
                "Target",
                "Machine"
            };
        }

        protected override string ToolName
        {
            get
            {
                switch (Flavor)
                {
                    case ToolFlavor.Zig:
                        return "zig.exe";
                    case ToolFlavor.LLVM:
                        return GetToolNameFromSubTool();
                    default:
                        throw new NotImplementedException($"unknown tool flavor {Flavor}");
                }
            }
        }

        //ZigRuntime.ResolveZigExecutableAsync().GetAwaiter().GetResult() ??
        //  throw new FileNotFoundException("Zig not found.");
        public abstract string SubTool { get; }

        private string GetToolNameFromSubTool()
        {
            switch (SubTool)
            {
                case "cc":
                    return "clang.exe";
                case "ar":
                    return "llvm-ar.exe";
                default:
                    throw new NotImplementedException();
            }
        }

        public enum ToolFlavor
        {
            Zig,
            LLVM
        }

        public ToolFlavor Flavor
        {
            get => (ToolFlavor)Enum.Parse(typeof(ToolFlavor), PropertyOrNull<string>());
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.String)
                    {
                        DisplayName = "Tool Flavor",
                        Description = "The tool flavor to use for the build.",
                    },
                    value.ToString()
                );
            }
        }

        public string Target
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.String)
                    {
                        DisplayName = "Target",
                        Description = "The target triple to build for.",
                        SwitchValue = "-target ",
                    },
                    value
                );
            }
        }

        // the sub tool must be on the command line or response files will not be processed
        protected override string GenerateCommandLineCommandsExceptSwitches(string[] switchesToRemove, CommandLineFormat format = CommandLineFormat.ForBuildLog, EscapeFormat escapeFormat = EscapeFormat.Default)
        {
            return SubTool;
        }

        [Required]
        public virtual ITaskItem[] Sources
        {
            get => PropertyOrNull<ITaskItem[]>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.ITaskItemArray)
                    {
                        Separator = " ",
                        Required = true,
                    },
                    value
                );
            }
        }

        protected override ITaskItem[] TrackedInputFiles => Sources;
        protected override Encoding ResponseFileEncoding => Encoding.ASCII;
        protected override Encoding StandardOutputEncoding => Encoding.UTF8;
        protected override Encoding StandardErrorEncoding => Encoding.UTF8;
    }
}
