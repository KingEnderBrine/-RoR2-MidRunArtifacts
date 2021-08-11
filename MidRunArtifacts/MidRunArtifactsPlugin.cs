using BepInEx;
using BepInEx.Logging;
using MonoMod.RuntimeDetour.HookGen;
using RoR2;
using RoR2.Networking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using System.Text.RegularExpressions;
using UnityEngine;

[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
namespace MidRunArtifacts
{
    [BepInPlugin("com.KingEnderBrine.MidRunArtifacts", "Mid Run Artifacts", "1.1.1")]
    public class MidRunArtifactsPlugin : BaseUnityPlugin
    {
        private static readonly ConstructorInfo autoCompleteCtor = typeof(RoR2.Console.AutoComplete).GetConstructor(new[] { typeof(RoR2.Console) });
        private static readonly MethodInfo consoleAwake = typeof(RoR2.Console).GetMethod(nameof(RoR2.Console.Awake), BindingFlags.NonPublic | BindingFlags.Instance);

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

        private void Awake()
        {
            Instance = this;

            HookEndpointManager.Add(consoleAwake, (Action<Action<RoR2.Console>, RoR2.Console>)RegisterCommands);
            HookEndpointManager.Add(autoCompleteCtor, (Action<Action<RoR2.Console.AutoComplete, RoR2.Console>, RoR2.Console.AutoComplete, RoR2.Console>)CommandArgsAutoCompletion);
        }

        private void Destroy()
        {
            Instance = null;

            HookEndpointManager.Remove(consoleAwake, (Action<Action<RoR2.Console>, RoR2.Console>)RegisterCommands);
            HookEndpointManager.Remove(autoCompleteCtor, (Action<Action<RoR2.Console.AutoComplete, RoR2.Console>, RoR2.Console.AutoComplete, RoR2.Console>)CommandArgsAutoCompletion);
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
            var artifactNames = ArtifactCatalog.artifactDefs.Select(GetArgNameForAtrifact);

            var result = new List<string>();
            result.AddRange(artifactNames.Select(el => $"mra_enable {el}"));
            result.AddRange(artifactNames.Select(el => $"mra_disable {el}"));
            result.AddRange(artifactNames.Select(el => $"mra_toggle {el}"));
            result.Sort();

            return result;
        }

        private static void RegisterCommands(Action<RoR2.Console> orig, RoR2.Console self)
        {
            try
            {
                self.concommandCatalog["mra_enable"] = new RoR2.Console.ConCommand
                {
                    action = CCEnable,
                    flags = ConVarFlags.SenderMustBeServer,
                    helpText = "Enable artifact"
                };

                self.concommandCatalog["mra_disable"] = new RoR2.Console.ConCommand
                {
                    action = CCDisable,
                    flags = ConVarFlags.SenderMustBeServer,
                    helpText = "Disable artifact"
                };

                self.concommandCatalog["mra_toggle"] = new RoR2.Console.ConCommand
                {
                    action = CCToggle,
                    flags = ConVarFlags.SenderMustBeServer,
                    helpText = "Toggle artifact"
                };
            }
            catch { }

            orig(self);
        }

        //[ConCommand(commandName = "mra_enable", flags = ConVarFlags.SenderMustBeServer, helpText = "Enable artifact")]
        private static void CCEnable(ConCommandArgs args) => ToggleArtifact(args, true);

        //[ConCommand(commandName = "mra_disable", flags = ConVarFlags.SenderMustBeServer, helpText = "Disable artifact")]
        private static void CCDisable(ConCommandArgs args) => ToggleArtifact(args, false);
        private static void CCToggle(ConCommandArgs args) => ToggleArtifact(args);

        private static void ToggleArtifact(ConCommandArgs args, bool? newState = null)
        {
            if (!RunArtifactManager.instance)
            {
                //Using Debug.Log because messages from InstanceLogger are not shown in in-game console
                Debug.Log("You can only use this command while in a run");
                return;
            }

            if (GameNetworkManager.singleton.desiredHost.hostingParameters.listen == true && !SteamworksLobbyManager.ownsLobby)
            {
                Debug.Log("You must be a lobby leader to use this command");
                return;
            }

            if (args.Count == 0)
            {
                Debug.Log("No arguments supplied");
                return;
            }

            var def = GetArtifactDefFromString(args[0]);

            if (!def)
            {
                Debug.Log("Artifact with a given name was not found.");
                return;
            }

            RunArtifactManager.instance.SetArtifactEnabledServer(def, newState ?? !RunArtifactManager.instance.IsArtifactEnabled(def));
        }

        private static ArtifactDef GetArtifactDefFromString(string partialName)
        {
            foreach (var artifact in ArtifactCatalog.artifactDefs)
            {
                if (GetArgNameForAtrifact(artifact).ToLower().Contains(partialName.ToLower()))
                {
                    return artifact;
                }
            }
            return null;
        }

        private static string GetArgNameForAtrifact(ArtifactDef artifactDef)
        {
            return Regex.Replace(Language.GetString(artifactDef.nameToken), "[ '-]", String.Empty);
        }
    }
}