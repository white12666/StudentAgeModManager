using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using System.Text;

namespace StudentAge.WorkshopBridge
{
    /// <summary>
    /// BepInEx 5 preloader patcher entry point. Initialize prepares Workshop links before
    /// Chainloader scans plugins. The Assembly-CSharp patch fixes the game's PathDefine static
    /// initialization race: PathDefine can be touched while SteamPlatform is not initialized,
    /// permanently binding SAVE_PATH to Saves/user even though Steam becomes ready moments later.
    /// </summary>
    public static class WorkshopBridgePatcher
    {
        private const string GameAssemblyName = "Assembly-CSharp.dll";
        private const string BridgeVersion = "0.4.0";

        public static IEnumerable<string> TargetDLLs
        {
            get { return new[] { GameAssemblyName }; }
        }

        public static void Initialize()
        {
            string gameRoot = null;
            try
            {
                gameRoot = LocateGameRoot();
                var result = WorkshopBridgeSynchronizer.Synchronize(gameRoot);
                WriteLog(gameRoot, result);
            }
            catch (Exception ex)
            {
                // A bridge failure must never prevent the game itself from starting.
                try
                {
                    WriteFatalLog(gameRoot, ex);
                }
                catch
                {
                    // ignored deliberately
                }
            }
        }

        // Required by the BepInEx 5 patcher discovery convention.
        public static void Patch(ref AssemblyDefinition assembly)
        {
            PatchGameSavePathInitialization(assembly);
            PatchAtomicModListSave(assembly);
        }

        internal static bool PatchAtomicModListSave(AssemblyDefinition assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            TypeDefinition modCtrl = assembly.MainModule.Types.SingleOrDefault(type =>
                string.Equals(type.FullName, "ModCtrl", StringComparison.Ordinal));
            if (modCtrl == null)
                throw new InvalidDataException("Assembly-CSharp 中缺少 ModCtrl。");
            MethodDefinition save = modCtrl.Methods.SingleOrDefault(method =>
                string.Equals(method.Name, "SaveModList", StringComparison.Ordinal) &&
                method.Parameters.Count == 0 && method.ReturnType.MetadataType == MetadataType.Void &&
                !method.IsStatic && !method.HasGenericParameters && method.HasBody);
            FieldDefinition activeMods = modCtrl.Fields.SingleOrDefault(field =>
                string.Equals(field.Name, "activeMods", StringComparison.Ordinal) &&
                field.FieldType.FullName == "System.Collections.Generic.List`1<System.UInt64>");
            if (save == null || activeMods == null)
                throw new InvalidDataException("ModCtrl.SaveModList 方法或 activeMods 字段形状缺失或不唯一。");

            MethodReference streamWriterConstructor = save.Body.Instructions
                .Where(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Newobj)
                .Select(instruction => instruction.Operand as MethodReference)
                .SingleOrDefault(method => method != null &&
                    method.DeclaringType.FullName == "System.IO.StreamWriter" &&
                    method.Name == ".ctor" && method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
            MethodReference writeLine = save.Body.Instructions
                .Select(instruction => instruction.Operand as MethodReference)
                .SingleOrDefault(method => method != null &&
                    method.DeclaringType.FullName == "System.IO.TextWriter" &&
                    method.Name == "WriteLine" && method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.UInt64);
            MethodReference getSavePath = save.Body.Instructions
                .Select(instruction => instruction.Operand as MethodReference)
                .SingleOrDefault(method => method != null && method.Name == "GetSavePath" &&
                    method.DeclaringType.FullName == "PathDefine" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                    method.ReturnType.MetadataType == MetadataType.String);
            bool hasModLiteral = save.Body.Instructions.Count(instruction =>
                instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr &&
                string.Equals(instruction.Operand as string, "_mod", StringComparison.Ordinal)) == 1;
            if (streamWriterConstructor == null || writeLine == null || getSavePath == null ||
                !hasModLiteral)
                throw new InvalidDataException("ModCtrl.SaveModList 的 StreamWriter 结构锚点缺失或不唯一。");

            MethodInfo helper = typeof(WorkshopBridgePatcher).GetMethod(
                nameof(AtomicSaveModList), BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(IEnumerable<ulong>) }, null);
            if (helper == null) throw new MissingMethodException(nameof(AtomicSaveModList));

            save.Body.ExceptionHandlers.Clear();
            save.Body.Variables.Clear();
            save.Body.Instructions.Clear();
            var il = save.Body.GetILProcessor();
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldstr, "_mod"));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Call, getSavePath));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ldfld, activeMods));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Call,
                assembly.MainModule.ImportReference(helper)));
            il.Append(il.Create(Mono.Cecil.Cil.OpCodes.Ret));
            return true;
        }

        public static void AtomicSaveModList(string path, IEnumerable<ulong> activeMods)
        {
            if (activeMods == null) throw new ArgumentNullException(nameof(activeMods));
            WorkshopBridgeSynchronizer.SaveCompleteModListSerialized(path, activeMods);
        }
        internal static bool PatchGameSavePathInitialization(AssemblyDefinition assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            TypeDefinition pathDefine = assembly.MainModule.Types.FirstOrDefault(type =>
                string.Equals(type.FullName, "PathDefine", StringComparison.Ordinal));
            if (pathDefine == null)
                throw new InvalidDataException("Assembly-CSharp 中缺少 PathDefine。");

            FieldDefinition savePath = pathDefine.Fields.FirstOrDefault(field =>
                string.Equals(field.Name, "SAVE_PATH", StringComparison.Ordinal));
            FieldDefinition testSavePath = pathDefine.Fields.FirstOrDefault(field =>
                string.Equals(field.Name, "TEST_SAVE_PATH", StringComparison.Ordinal));
            FieldDefinition imagePath = pathDefine.Fields.FirstOrDefault(field =>
                string.Equals(field.Name, "IMG_PATH", StringComparison.Ordinal));
            FieldDefinition musicPath = pathDefine.Fields.FirstOrDefault(field =>
                string.Equals(field.Name, "MUSIC_PATH", StringComparison.Ordinal));
            if (savePath == null || testSavePath == null || imagePath == null || musicPath == null)
                throw new InvalidDataException("PathDefine 存档路径字段不完整。");

            TypeDefinition main = assembly.MainModule.Types.FirstOrDefault(type =>
                string.Equals(type.FullName, "Main", StringComparison.Ordinal));
            MethodDefinition onInit = main == null ? null : main.Methods.FirstOrDefault(method =>
                string.Equals(method.Name, "OnInit", StringComparison.Ordinal) &&
                method.Parameters.Count == 0 && method.HasBody);
            if (onInit == null)
                throw new InvalidDataException("Assembly-CSharp 中缺少 Main.OnInit()。");
            Mono.Cecil.Cil.Instruction userIdLog = onInit.Body.Instructions
                .FirstOrDefault(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr &&
                    string.Equals(instruction.Operand as string, "UserId:", StringComparison.Ordinal));
            if (userIdLog == null)
                throw new InvalidDataException("Main.OnInit() 中缺少 Steam 用户初始化完成锚点。");

            TypeDefinition platform = assembly.MainModule.Types.FirstOrDefault(type =>
                string.Equals(type.FullName, "Sdk.PlatformAPI.Platform", StringComparison.Ordinal));
            MethodDefinition current = platform == null ? null : platform.Methods.FirstOrDefault(method =>
                string.Equals(method.Name, "get_Current", StringComparison.Ordinal) &&
                method.Parameters.Count == 0);
            TypeDefinition basePlatform = assembly.MainModule.Types.FirstOrDefault(type =>
                string.Equals(type.FullName, "Sdk.PlatformAPI.BasePlatform", StringComparison.Ordinal));
            MethodDefinition getUserId = basePlatform == null ? null : basePlatform.Methods
                .FirstOrDefault(method => string.Equals(method.Name, "GetUserId",
                    StringComparison.Ordinal) && method.Parameters.Count == 0);
            MethodReference persistentDataPath = FindPropertyGetter(assembly,
                "UnityEngine.Application", "get_persistentDataPath");
            MethodReference combine3 = assembly.MainModule.ImportReference(typeof(Path)
                .GetMethod("Combine", new[] { typeof(string), typeof(string), typeof(string) }));
            MethodReference combine2 = assembly.MainModule.ImportReference(typeof(Path)
                .GetMethod("Combine", new[] { typeof(string), typeof(string) }));
            if (current == null || getUserId == null || persistentDataPath == null ||
                combine3 == null || combine2 == null)
                throw new InvalidDataException("PathDefine 修补依赖的方法不完整。");

            var injected = new List<Mono.Cecil.Cil.Instruction>();
            AddRootAssignment(injected, assembly, persistentDataPath, current, getUserId,
                combine3, "Saves", savePath);
            AddRootAssignment(injected, assembly, persistentDataPath, current, getUserId,
                combine3, "Saves_Test", testSavePath);
            AddChildAssignment(injected, combine2, savePath, "Images", imagePath);
            AddChildAssignment(injected, combine2, savePath, "Musics", musicPath);

            var il = onInit.Body.GetILProcessor();
            foreach (Mono.Cecil.Cil.Instruction instruction in injected)
                il.InsertBefore(userIdLog, instruction);
            RedirectBranches(onInit, userIdLog, injected[0]);
            return true;
        }

        private static MethodReference FindPropertyGetter(AssemblyDefinition assembly,
            string typeName, string methodName)
        {
            foreach (AssemblyNameReference reference in assembly.MainModule.AssemblyReferences)
            {
                AssemblyDefinition dependency;
                try { dependency = assembly.MainModule.AssemblyResolver.Resolve(reference); }
                catch { continue; }
                TypeDefinition type = dependency.MainModule.GetType(typeName);
                MethodDefinition method = type == null ? null : type.Methods.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.Ordinal) &&
                    candidate.Parameters.Count == 0);
                if (method != null) return assembly.MainModule.ImportReference(method);
            }
            return null;
        }

        private static void AddRootAssignment(ICollection<Mono.Cecil.Cil.Instruction> output,
            AssemblyDefinition assembly, MethodReference persistentDataPath,
            MethodDefinition current, MethodDefinition getUserId, MethodReference combine,
            string directoryName, FieldDefinition target)
        {
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call,
                persistentDataPath));
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldstr,
                directoryName));
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call,
                assembly.MainModule.ImportReference(current)));
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Callvirt,
                assembly.MainModule.ImportReference(getUserId)));
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, combine));
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Stsfld, target));
        }

        private static void AddChildAssignment(ICollection<Mono.Cecil.Cil.Instruction> output,
            MethodReference combine, FieldDefinition root, string child, FieldDefinition target)
        {
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldsfld, root));
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldstr, child));
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, combine));
            output.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Stsfld, target));
        }

        private static void RedirectBranches(MethodDefinition method,
            Mono.Cecil.Cil.Instruction oldTarget, Mono.Cecil.Cil.Instruction newTarget)
        {
            foreach (Mono.Cecil.Cil.Instruction instruction in method.Body.Instructions)
            {
                if (ReferenceEquals(instruction.Operand, oldTarget))
                {
                    instruction.Operand = newTarget;
                    continue;
                }
                var targets = instruction.Operand as Mono.Cecil.Cil.Instruction[];
                if (targets == null) continue;
                for (int index = 0; index < targets.Length; index++)
                    if (ReferenceEquals(targets[index], oldTarget)) targets[index] = newTarget;
            }
        }

        public static void Finish()
        {
        }

        private static string LocateGameRoot()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            var patchersDirectory = Path.GetDirectoryName(location);
            var bepinExDirectory = Directory.GetParent(patchersDirectory ?? string.Empty);
            var gameDirectory = bepinExDirectory == null ? null : bepinExDirectory.Parent;
            if (gameDirectory == null)
                throw new DirectoryNotFoundException("无法从补丁位置确定游戏目录: " + location);
            return gameDirectory.FullName;
        }

        private static void WriteLog(string gameRoot, BridgeResult result)
        {
            var logPath = GetLogPath(gameRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            using (var writer = new StreamWriter(logPath, false))
            {
                writer.WriteLine("StudentAge Workshop Bridge " + BridgeVersion);
                writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                writer.WriteLine("Synchronized: " + result.Synchronized);
                writer.WriteLine("Enabled IDs: " + result.EnabledIdCount);
                writer.WriteLine("Baseline IDs: " + result.BaselineIdCount);
                writer.WriteLine("Auto-enabled IDs: " + result.AutoEnabledIdCount);
                writer.WriteLine("Linked: " + result.LinkedCount);
                writer.WriteLine("Removed stale links: " + result.RemovedLinkCount);
                writer.WriteLine("Skipped: " + result.SkippedCount);
                writer.WriteLine("Errors: " + result.ErrorCount);
                writer.WriteLine();
                foreach (var message in result.Messages)
                    writer.WriteLine(message);
            }
        }

        private static void WriteFatalLog(string gameRoot, Exception ex)
        {
            var logPath = GetLogPath(gameRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            File.WriteAllText(logPath,
                "StudentAge Workshop Bridge failed before synchronization.\r\n" +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n\r\n" + ex);
        }

        private static string GetLogPath(string gameRoot)
        {
            var root = string.IsNullOrEmpty(gameRoot) ? AppDomain.CurrentDomain.BaseDirectory : gameRoot;
            return Path.Combine(root, "BepInEx", "WorkshopBridge.log");
        }
    }
}
