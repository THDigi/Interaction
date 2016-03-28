using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Sandbox.Common;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Common.Utils;
using VRage.Input;
using VRage.Utils;
using VRageMath;
using VRage;
using Digi.Utils;
using VRageRender;

namespace Digi.Interaction
{
    public class Settings
    {
        private const string FILE = "settings.cfg";
        
        public bool cables = true;
        public double cableWidth = 0.01;
        public int cableModel = 3;
        public int cableResolution = 30;
        public float interactionSounds = 0.3f;
        
        private static char[] CHARS = new char[] { '=' };
        
        public bool firstLoad = false;
        
        public Settings()
        {
            // load the settings if they exist
            if(!Load())
            {
                firstLoad = true; // config didn't exist, assume it's the first time the mod is loaded
            }
            
            Save(); // refresh config in case of any missing or extra settings
        }
        
        public bool Load()
        {
            try
            {
                if(MyAPIGateway.Utilities.FileExistsInLocalStorage(FILE, typeof(Settings)))
                {
                    var file = MyAPIGateway.Utilities.ReadFileInLocalStorage(FILE, typeof(Settings));
                    ReadSettings(file);
                    file.Close();
                    return true;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
            
            return false;
        }
        
        private void ReadSettings(TextReader file)
        {
            try
            {
                string line;
                string[] args;
                int i;
                bool b;
                float f;
                double d;
                
                while((line = file.ReadLine()) != null)
                {
                    if(line.Length == 0)
                        continue;
                    
                    i = line.IndexOf("//");
                    
                    if(i > -1)
                        line = (i == 0 ? "" : line.Substring(0, i));
                    
                    if(line.Length == 0)
                        continue;
                    
                    args = line.Split(CHARS, 2);
                    
                    if(args.Length != 2)
                    {
                        Log.Error("Unknown "+FILE+" line: "+line+"\nMaybe is missing the '=' ?");
                        continue;
                    }
                    
                    args[0] = args[0].Trim().ToLower();
                    args[1] = args[1].Trim().ToLower();
                    
                    switch(args[0])
                    {
                        case "cables":
                            if(bool.TryParse(args[1], out b))
                                cables = b;
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
                            continue;
                        case "cablewidth":
                            if(double.TryParse(args[1], out d))
                                cableWidth = MathHelper.Clamp(d, 0.00001, 1);
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
                            continue;
                        case "cablemodel":
                            if(int.TryParse(args[1], out i))
                                cableModel = MathHelper.Clamp(i, 0, 3);
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
                            continue;
                        case "cableresolution":
                            if(int.TryParse(args[1], out i))
                                cableResolution = MathHelper.Clamp(i, 5, 100);
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
                            continue;
                        case "interactionsounds":
                            if(float.TryParse(args[1], out f))
                                interactionSounds = (float)Math.Round(MathHelper.Clamp(f, 0, 1), 5);
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
                            continue;
                    }
                }
                
                Log.Info("Loaded settings:\n" + GetSettingsString(false));
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public void Save()
        {
            try
            {
                var file = MyAPIGateway.Utilities.WriteFileInLocalStorage(FILE, typeof(Settings));
                file.Write(GetSettingsString(true));
                file.Flush();
                file.Close();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public string GetSettingsString(bool comments)
        {
            var str = new StringBuilder();
            
            if(comments)
            {
                str.AppendLine("// Animated Interaction mod config; this file gets automatically overwritten after being loaded so don't leave custom comments.");
                str.AppendLine("// You can reload this while the game is running by typing in chat: /interaction reload");
                str.AppendLine("// Lines starting with // are comments. All values are case insensitive unless otherwise specified.");
                str.AppendLine();
            }
            
            str.Append("Cables=").Append(cables ? "true" : "false").AppendLine(comments ? " // Show interaction cables?" : "");
            str.Append("CableWidth=").Append(cableWidth).AppendLine(comments ? " // Cable width in metres. Values from 0.00001 to 1, default: 0.01" : "");
            str.Append("CableModel=").Append(cableModel).AppendLine(comments ? " // Cable type, 0 = sprite, 1 to 3 is a model and controls the quality of the mesh. default: 3" : "");
            str.Append("CableResolution=").Append(cableResolution).AppendLine(comments ? " // Interaction cable smoothness. Values from 5 to 100, default: 30" : "");
            str.Append("InteractionSounds=").Append(interactionSounds.ToString("0.#####")).AppendLine(comments ? " // Sounds coming from other players interacting. They're already scaled to your game volume, this stacks on top of that. Values from 0 (turned off) to 1, default: 0.3" : "");
            
            return str.ToString();
        }
        
        public void Close()
        {
        }
    }
}