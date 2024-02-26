using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using MonoMod.RuntimeDetour.HookGen;
using RoR2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Permissions;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using static HG.Reflection.SearchableAttribute;

[assembly: OptIn()]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
[assembly: AssemblyVersion(MidRunArtifacts.MidRunArtifactsPlugin.Version)]
namespace MidRunArtifacts
{

    [BepInPlugin(GUID, Name, Version)]
    public class MidRunArtifactsPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.KingEnderBrine.MidRunArtifacts";
        public const string Name = "Mid Run Artifacts";
        public const string Version = "1.2.1";

        private static readonly ConstructorInfo autoCompleteCtor = typeof(RoR2.Console.AutoComplete).GetConstructor(new[] { typeof(RoR2.Console) });
        private static readonly MethodInfo chatAddMessage = typeof(Chat).GetMethod(nameof(Chat.CCSay), BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(ConCommandArgs)}, null);

        internal static MidRunArtifactsPlugin Instance { get; private set; }
        internal static ManualLogSource InstanceLogger { get => Instance?.Logger; }

        private static List<string> commandsAutocomplete;
        private static string autocompleteLanguage;
        private static List<string> CommandsAutocomplete 
        { 
            get
            {
                if (autocompleteLanguage == Language.currentLanguageName)
                {
                    return commandsAutocomplete;
                }
                autocompleteLanguage = Language.currentLanguageName;
                return commandsAutocomplete = GatherCommandsAutocomplete();
            }
        }

        public static ConfigEntry<bool> EnableChatCommands { get; set; }

        private void Awake()
        {
            Instance = this;

            EnableChatCommands = Config.Bind("Main", "EnableChatCommands", false, "Accept commands from any player in chat");

            HookEndpointManager.Add(autoCompleteCtor, (Action<Action<RoR2.Console.AutoComplete, RoR2.Console>, RoR2.Console.AutoComplete, RoR2.Console>)CommandArgsAutoCompletion);
            if (EnableChatCommands.Value)
            {
                HookEndpointManager.Add(chatAddMessage, (Action<Action<ConCommandArgs>, ConCommandArgs>)OnChatAddMessage);
            }
        }

        private void Destroy()
        {
            Instance = null;

            HookEndpointManager.Remove(autoCompleteCtor, (Action<Action<RoR2.Console.AutoComplete, RoR2.Console>, RoR2.Console.AutoComplete, RoR2.Console>)CommandArgsAutoCompletion);
            if (EnableChatCommands.Value)
            {
                HookEndpointManager.Remove(chatAddMessage, (Action<Action<ConCommandArgs>, ConCommandArgs>)OnChatAddMessage);
            }
        }

        private static void OnChatAddMessage(Action<ConCommandArgs> orig, ConCommandArgs args)
        {
            orig(args);

            if (!NetworkServer.active || !RunArtifactManager.instance)
            {
                return;
            }

            if (!args[0].StartsWith("/"))
            {
                return;
            }

            var lexer = new RoR2.Console.Lexer(args[0]);
            var command = lexer.NextToken();
            if (command is null)
            {
                return;
            }

            bool? newState;
            switch (command)
            {
                case "mra_enable":
                    newState = true;
                    break;
                case "mra_disable":
                    newState = false;
                    break;
                case "mra_toggle":
                    newState = null;
                    break;
                default:
                    return;
            }

            var userArgs = new List<string>();
            while (true)
            {
                var arg = lexer.NextToken();
                if (arg is null || arg == ";")
                {
                    break;
                }
                userArgs.Add(arg);
            }

            ToggleArtifact(new ConCommandArgs
            {
                userArgs = userArgs,
                commandName = command,
                sender = args.sender
            }, newState, true);
        }

        private static void CommandArgsAutoCompletion(Action<RoR2.Console.AutoComplete, RoR2.Console> orig, RoR2.Console.AutoComplete self, RoR2.Console console)
        {
            orig(self, console);

            try
            {
                self.searchableStrings.AddRange(CommandsAutocomplete);
            }
            catch (Exception e)
            {
                InstanceLogger.LogWarning("Failed to gather autocomplete options");
                InstanceLogger.LogError(e);
            }
        }

        private static List<string> GatherCommandsAutocomplete()
        {
            var artifactNames = ArtifactCatalog.artifactDefs.Select(a => GetArgNameForArtifact(a, false));
            if (Language.currentLanguage.name != Language.english.name)
            {
                artifactNames = artifactNames.Union(ArtifactCatalog.artifactDefs.Select(a => GetArgNameForArtifact(a, true)));
            }

            var result = new List<string>();
            result.AddRange(artifactNames.Select(el => $"mra_enable {el}"));
            result.AddRange(artifactNames.Select(el => $"mra_disable {el}"));
            result.AddRange(artifactNames.Select(el => $"mra_toggle {el}"));
            result.Sort();

            return result;
        }

        [ConCommand(commandName = "mra_enable", flags = ConVarFlags.SenderMustBeServer, helpText = "Enable artifact")]
        private static void CCEnable(ConCommandArgs args) => ToggleArtifact(args, true);

        [ConCommand(commandName = "mra_disable", flags = ConVarFlags.SenderMustBeServer, helpText = "Disable artifact")]
        private static void CCDisable(ConCommandArgs args) => ToggleArtifact(args, false);

        [ConCommand(commandName = "mra_toggle", flags = ConVarFlags.SenderMustBeServer, helpText = "Toggle artifact")]
        private static void CCToggle(ConCommandArgs args) => ToggleArtifact(args);

        private static void ToggleArtifact(ConCommandArgs args, bool? newState = null, bool fromChat = false)
        {
            if (!RunArtifactManager.instance)
            {
                //Using Debug.Log because messages from InstanceLogger are not shown in in-game console
                LogMessage("You can only use this command while in a run");
                return;
            }

            if (!NetworkServer.active)
            {
                LogMessage("You must be a host to use this command");
                return;
            }

            if (args.Count == 0)
            {
                LogMessage("No arguments supplied");
                return;
            }

            var def = GetArtifactDefFromString(args[0]);

            if (!def)
            {
                LogMessage("Artifact with a given name was not found.");
                return;
            }

            RunArtifactManager.instance.SetArtifactEnabledServer(def, newState ?? !RunArtifactManager.instance.IsArtifactEnabled(def));

            void LogMessage(string message)
            {
                if (fromChat)
                {
                    Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                    {
                        baseToken = message
                    });
                }
                else
                {
                    Debug.Log(message);
                }
            }
        }

        private static ArtifactDef GetArtifactDefFromString(string partialName)
        {
            foreach (var artifact in ArtifactCatalog.artifactDefs)
            {
                if (GetArgNameForArtifact(artifact, false).ToLower().Contains(partialName.ToLower()) ||
                    (Language.english.name != Language.currentLanguage.name && GetArgNameForArtifact(artifact, true).ToLower().Contains(partialName.ToLower())))
                {
                    return artifact;
                }
            }
            return null;
        }

        private static string GetArgNameForArtifact(ArtifactDef artifactDef, bool english)
        {
            return Regex.Replace(Language.GetString(artifactDef.nameToken, english ? Language.english.name : Language.currentLanguage.name), "[ '-]", String.Empty);
        }
    }
}