﻿using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Components;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.Input;
using VRage.ObjectBuilders;
using VRage.Game.Components;
using VRage.ModAPI;
using VRageMath;

using Digi.Utils;

namespace Digi.Interaction
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class InteractionMod : MySessionComponentBase
    {
        public override void LoadData()
        {
            Log.SetUp("Animated Interaction", 652337022, "Interaction");
            instance = this;
        }

        private const string MOD_DEV_NAME = "Interaction.dev";
        private const int WORKSHOP_DEV_ID = 650441224;

        public enum InteractionType
        {
            START,
            START_TARGET,
            END,
            END_TARGET,
            CLICK,
            TYPE,
            TAB,
            RC_ON,
            RC_OFF,
            CAMERA_ON,
            CAMERA_OFF,
            USE_BUTTON,
            USE_HOLD_ON,
            USE_HOLD_OFF,
        }

        public class InteractionEffect
        {
            public string animation = null;
            public MyFrameOption frameOption = MyFrameOption.StayOnLastFrame;
            public float timeScale = 1f;
            public string sound = null;
            public long delayTicks = 0;

            private MyAnimationCommand animCmd = new MyAnimationCommand()
            {
                PlaybackCommand = MyPlaybackCommand.Play,
                BlendOption = MyBlendOption.Immediate,
                BlendTime = 0.25f,
            };

            public InteractionEffect() { }

            public void PlayAnimation(MySkinnedEntity skinned, bool forceStayOnLastFrame = false)
            {
                if(skinned.UseNewAnimationSystem)
                {
                    var character = skinned as IMyCharacter;
                    character.TriggerCharacterAnimationEvent(animation, true);
                }
                else
                {
                    animCmd.AnimationSubtypeName = animation;
                    animCmd.TimeScale = timeScale;
                    animCmd.FrameOption = (forceStayOnLastFrame ? MyFrameOption.StayOnLastFrame : frameOption);
                    skinned.AddCommand(animCmd, true);
                }
            }
        }

        public class Interacted
        {
            public long target;
            public long time;

            public Vector3? offset = null;
            public bool remove = false;
            public byte skipGravity = SKIP_TICKS_GRAVITY;
            public bool inGravity = false;
            public Vector3 gravity;

            public List<CableJoint> cable = null;
            public bool cableAtFingers = false;

            public Interacted() { }

            public void Clear()
            {
                if(cable != null)
                {
                    foreach(var c in cable)
                    {
                        if(c.ents != null)
                        {
                            foreach(var e in c.ents)
                            {
                                e.Close();
                            }

                            c.ents = null;
                        }
                    }

                    cable = null;
                }
            }
        }

        public class CableJoint
        {
            public Vector3D position;
            public Vector3 velocity = Vector3.Zero;
            public Vector3 prevDir = Vector3.Zero;
            public List<MyEntity> ents = null;

            public CableJoint() { }
        }

        public static InteractionMod instance = null;

        public bool init = false;
        public bool isThisHostDedicated = false;

        public Settings settings = null;

        private IMyCharacter characterEntity = null;

        private bool lcdShown = false;
        private bool lastInRC = false;
        private bool lastInCamera = false;
        private bool lastHoldingUse = false;
        private long startTime = 0;
        private long lastInteraction = 0;
        private MyTerminalPageEnum lastPage = MyTerminalPageEnum.None;
        private short skipPlanets = SKIP_TICKS_PLANETS;
        private string pathToMod = "";

        private Dictionary<long, Interacted> interactEntity = new Dictionary<long, Interacted>();
        private List<long> removeInteractEntity = new List<long>();

        private List<MyKeys> keys = new List<MyKeys>();
        private HashSet<MyKeys> prevKeys = new HashSet<MyKeys>();

        public Dictionary<long, MyPlanet> planets = new Dictionary<long, MyPlanet>();
        public Dictionary<long, IMyGravityProvider> gravityGenerators = new Dictionary<long, IMyGravityProvider>();

        private static readonly List<IMyPlayer> players = new List<IMyPlayer>(0);
        private static readonly HashSet<IMyEntity> ents = new HashSet<IMyEntity>();
        private static readonly List<MyEntity> detectableEnts = new List<MyEntity>();
        private static readonly List<IHitInfo> hits = new List<IHitInfo>();
        private static BoundingSphereD sphere = new BoundingSphereD(Vector3D.Zero, MyConstants.DEFAULT_INTERACTIVE_DISTANCE);

        private const ushort PACKET = 12004;
        private static readonly Encoding encode = Encoding.Unicode;
        private const char SEPARATOR = ' ';

        private const long DELAY_GRABCABLE = TimeSpan.TicksPerMillisecond * 200;
        private const long DELAY_CONNECT = TimeSpan.TicksPerMillisecond * 800;

        private const long DELAY_DISCONNECT = TimeSpan.TicksPerMillisecond * 300;
        private const long DELAY_PUTCABLE = TimeSpan.TicksPerMillisecond * 800;

        private const int PACKET_SOUND_DISTANCE = 100 * 100;
        private const int CABLE_DRAWDISTANCE_SQ = 1000 * 1000;

        private const byte SKIP_TICKS_GRAVITY = 10;
        private const short SKIP_TICKS_PLANETS = 600;

        private const string BONE_RIGHTHAND_INDEXTIP = "SE_RigR_Index_3";
        private const string BONE_LEFTARM_LCD = "SE_RigLForearm3";

        private static readonly Vector4 CABLE_COLOR = new Color(15, 15, 15).ToVector4();

        private readonly Dictionary<InteractionType, InteractionEffect> interactionEffect = new Dictionary<InteractionType, InteractionEffect>()
        {
            {InteractionType.START, new InteractionEffect()
                {
                    animation = "interactionstart",
                    sound = "PlayerLCDStart",
                    delayTicks = TimeSpan.TicksPerMillisecond * 400,
                }
            },

            {InteractionType.START_TARGET, new InteractionEffect()
                {
                    animation = "interactionstarttarget",
                    sound = "PlayerLCDStart",
                    delayTicks = TimeSpan.TicksPerMillisecond * 1200,
                }
            },

            {InteractionType.END, new InteractionEffect()
                {
                    animation = "interactionend",
                    sound = "PlayerLCDEnd",
                    frameOption = MyFrameOption.PlayOnce,
                }
            },

            {InteractionType.END_TARGET, new InteractionEffect()
                {
                    animation = "interactionendtarget",
                    sound = "PlayerLCDEnd",
                    frameOption = MyFrameOption.PlayOnce,
                }
            },

            {InteractionType.CLICK, new InteractionEffect()
                {
                    animation = "interactionclick",
                    sound = "PlayerLCDClick",
                    delayTicks = TimeSpan.TicksPerMillisecond * 250,
                }
            },

            {InteractionType.TYPE, new InteractionEffect()
                {
                    animation = "interactiontype",
                    sound = "PlayerLCDType",
                    delayTicks = TimeSpan.TicksPerMillisecond * 1000,
                }
            },

            {InteractionType.TAB, new InteractionEffect()
                {
                    animation = "interactiontab",
                    sound = "PlayerLCDTab",
                    delayTicks = TimeSpan.TicksPerMillisecond * 350,
                }
            },

            {InteractionType.RC_ON, new InteractionEffect()
                {
                    animation = "interactionrcon",
                }
            },

            {InteractionType.RC_OFF, new InteractionEffect()
                {
                    animation = "interactionrcoff",
                    frameOption = MyFrameOption.PlayOnce,
                    delayTicks = TimeSpan.TicksPerMillisecond * 300,
                }
            },

            {InteractionType.CAMERA_ON, new InteractionEffect()
                {
                    animation = "interactioncameraon",
                }
            },

            {InteractionType.CAMERA_OFF, new InteractionEffect()
                {
                    animation = "interactioncameraoff",
                    frameOption = MyFrameOption.PlayOnce,
                    delayTicks = TimeSpan.TicksPerMillisecond * 300,
                }
            },

            {InteractionType.USE_BUTTON, new InteractionEffect()
                {
                    animation = "interactionusebutton",
                    frameOption = MyFrameOption.PlayOnce,
                    delayTicks = TimeSpan.TicksPerMillisecond * 900,
                    timeScale = 0.8f,
                }
            },

            {InteractionType.USE_HOLD_ON, new InteractionEffect()
                {
                    animation = "interactionusebuttonon",
                    frameOption = MyFrameOption.StayOnLastFrame,
                    delayTicks = TimeSpan.TicksPerMillisecond * 900,
                    timeScale = 0.8f,
                }
            },

            {InteractionType.USE_HOLD_OFF, new InteractionEffect()
                {
                    animation = "interactionusebuttonoff",
                    frameOption = MyFrameOption.PlayOnce,
                    timeScale = 0.8f,
                }
            },
        };

        private readonly HashSet<string> useObjects = new HashSet<string>()
        {
            "MyUseObjectPanelButton",
            "MyUseObjectDoorTerminal",
            "MyUseObjectAdvancedDoorTerminal",
            "MyUseObjectAirtightDoors",
        };

        private readonly HashSet<string> useHoldObjects = new HashSet<string>()
        {
            "MyUseObjectMedicalRoom",
        };

        public void Init()
        {
            init = true;
            isThisHostDedicated = (MyAPIGateway.Multiplayer.IsServer && MyAPIGateway.Utilities.IsDedicated);

            Log.Init();
            Log.Info("Initialized");

            if(!isThisHostDedicated)
            {
                settings = new Settings();

                MyAPIGateway.Utilities.MessageEntered += MessageEntered;
                MyAPIGateway.Multiplayer.RegisterMessageHandler(PACKET, ReceivedPacket);

                var mods = MyAPIGateway.Session.GetCheckpoint("").Mods;
                bool found = false;

                foreach(var mod in mods)
                {
                    if(mod.PublishedFileId == 0 ? mod.Name == MOD_DEV_NAME : (mod.PublishedFileId == Log.workshopId || mod.PublishedFileId == WORKSHOP_DEV_ID))
                    {
                        pathToMod = MyAPIGateway.Utilities.GamePaths.ModsPath + @"\" + mod.Name + @"\";
                        found = true;
                        break;
                    }
                }

                if(!found)
                    Log.Error("Can't find mod " + Log.workshopId + ".sbm or " + MOD_DEV_NAME + " in the mod list!");
            }
        }

        protected override void UnloadData()
        {
            try
            {
                if(init)
                {
                    init = false;

                    interactEntity.Clear();
                    removeInteractEntity.Clear();

                    prevKeys.Clear();
                    keys.Clear();

                    planets.Clear();
                    gravityGenerators.Clear();

                    if(!isThisHostDedicated)
                    {
                        if(settings != null)
                        {
                            settings.Close();
                            settings = null;
                        }

                        MyAPIGateway.Utilities.MessageEntered -= MessageEntered;
                        MyAPIGateway.Multiplayer.UnregisterMessageHandler(PACKET, ReceivedPacket);
                    }

                    Log.Info("Mod unloaded");
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }

            Log.Close();
        }

        public void MessageEntered(string msg, ref bool send)
        {
            try
            {
                if(msg.StartsWith("/interaction", StringComparison.InvariantCultureIgnoreCase))
                {
                    send = false;
                    msg = msg.Substring("/interaction".Length).Trim().ToLower();

                    if(msg.Equals("reload"))
                    {
                        if(settings.Load())
                            MyAPIGateway.Utilities.ShowMessage(Log.modName, "Reloaded and re-saved config.");
                        else
                            MyAPIGateway.Utilities.ShowMessage(Log.modName, "Config created with the current settings.");

                        // reset cables
                        foreach(var i in interactEntity.Values)
                        {
                            i.Clear();
                        }

                        settings.Save();
                        return;
                    }

                    MyAPIGateway.Utilities.ShowMessage(Log.modName, "Available commands:");
                    MyAPIGateway.Utilities.ShowMessage("/interaction reload ", "reloads the config file.");
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void ReceivedPacket(byte[] bytes)
        {
            try
            {
                int index = 0;

                var type = (InteractionType)bytes[index];
                index += sizeof(byte);

                long entId = BitConverter.ToInt64(bytes, index);
                index += sizeof(long);

                if(!MyAPIGateway.Entities.EntityExists(entId))
                    return;

                var ent = MyAPIGateway.Entities.GetEntityById(entId);
                var skinned = ent as MySkinnedEntity;

                if(skinned == null)
                    return;

                if(bytes.Length > index)
                {
                    long targetId = BitConverter.ToInt64(bytes, index);
                    index += sizeof(long);

                    if(bytes.Length > index)
                    {
                        var pos = new Vector3(BitConverter.ToSingle(bytes, index),
                                              BitConverter.ToSingle(bytes, index + sizeof(float)),
                                              BitConverter.ToSingle(bytes, index + sizeof(float) * 2));
                        index += sizeof(float) * 3;

                        InternalInteraction(ent, type, targetId, pos);
                    }
                    else
                        InternalInteraction(ent, type, targetId);
                }
                else
                {
                    InternalInteraction(ent, type);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void TriggerInteraction(MySkinnedEntity skinned, InteractionType type, IMyEntity target = null, Vector3? offset = null, bool forceStayOnLastFrame = false)
        {
            try
            {
                var effect = interactionEffect[type];

                effect.PlayAnimation(skinned, forceStayOnLastFrame);

                if(target != null)
                    InternalInteraction(skinned, type, target.EntityId, offset);
                else
                    InternalInteraction(skinned, type);

                if(effect.sound != null)
                {
                    int len = sizeof(byte) + sizeof(long);

                    if(target != null)
                    {
                        len += sizeof(long);

                        if(offset.HasValue)
                            len += sizeof(float) * 3;
                    }

                    var bytes = new byte[len];
                    bytes[0] = (byte)type; // interaction type
                    len = 1;

                    var data = BitConverter.GetBytes(skinned.EntityId);
                    Array.Copy(data, 0, bytes, len, data.Length);
                    len += data.Length;

                    if(target != null)
                    {
                        data = BitConverter.GetBytes(target.EntityId);
                        Array.Copy(data, 0, bytes, len, data.Length);
                        len += data.Length;

                        if(offset.HasValue)
                        {
                            data = BitConverter.GetBytes(offset.Value.X);
                            Array.Copy(data, 0, bytes, len, data.Length);
                            len += data.Length;

                            data = BitConverter.GetBytes(offset.Value.Y);
                            Array.Copy(data, 0, bytes, len, data.Length);
                            len += data.Length;

                            data = BitConverter.GetBytes(offset.Value.Z);
                            Array.Copy(data, 0, bytes, len, data.Length);
                            len += data.Length;
                        }
                    }

                    var pos = skinned.WorldMatrix.Translation;
                    bool checkDistance = true;

                    switch(type)
                    {
                        case InteractionType.START:
                        case InteractionType.START_TARGET:
                        case InteractionType.END:
                        case InteractionType.END_TARGET:
                            checkDistance = false;
                            break;
                    }

                    MyAPIGateway.Players.GetPlayers(players, delegate (IMyPlayer p)
                                                    {
                                                        if(p.SteamUserId != MyAPIGateway.Multiplayer.MyId)
                                                        {
                                                            if(checkDistance && Vector3D.DistanceSquared(p.GetPosition(), pos) > PACKET_SOUND_DISTANCE)
                                                                return false;

                                                            MyAPIGateway.Multiplayer.SendMessageTo(PACKET, bytes, p.SteamUserId);
                                                        }

                                                        return false;
                                                    });
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void InternalInteraction(IMyEntity ent, InteractionType type, long targetId = 0, Vector3? offset = null)
        {
            if(type == InteractionType.START_TARGET)
            {
                if(targetId == 0)
                    return;

                if(!interactEntity.ContainsKey(ent.EntityId))
                {
                    interactEntity.Add(ent.EntityId, new Interacted()
                    {
                        target = targetId,
                        offset = offset,
                        time = DateTime.UtcNow.Ticks,
                    });
                }
                else
                {
                    var interact = interactEntity[ent.EntityId];
                    interact.remove = false;
                    interact.target = targetId;
                    interact.offset = offset;
                    interact.time = DateTime.UtcNow.Ticks;
                    interact.cableAtFingers = false;
                    interact.Clear();
                }

                PlaySound(ent, "PlayerLCDStartTarget");
                return;
            }
            else if(type == InteractionType.END_TARGET)
            {
                if(interactEntity.ContainsKey(ent.EntityId))
                {
                    interactEntity[ent.EntityId].remove = true;
                    interactEntity[ent.EntityId].time = DateTime.UtcNow.Ticks;
                    PlaySound(ent, "PlayerLCDEndTarget");
                    return;
                }
            }

            if(characterEntity != null && characterEntity.EntityId == ent.EntityId)
                return; // block click/tab/type sounds from your own menus

            string sound = interactionEffect[type].sound;

            if(sound != null && settings.interactionSounds > 0)
                PlaySound(ent, sound);
        }

        private bool InteractLoop(long charId, Interacted interacted)
        {
            if(!MyAPIGateway.Entities.EntityExists(charId) || !MyAPIGateway.Entities.EntityExists(interacted.target))
                return false;

            var charEnt = MyAPIGateway.Entities.GetEntityById(charId) as MySkinnedEntity;

            if(charEnt == null)
                return false;

            if(Vector3D.DistanceSquared(MyAPIGateway.Session.Camera.WorldMatrix.Translation, charEnt.WorldMatrix.Translation) > CABLE_DRAWDISTANCE_SQ)
            {
                interacted.Clear();
                return true;
            }

            long time = DateTime.UtcNow.Ticks;
            bool targetFinger = false;

            if(interacted.remove)
            {
                if(interacted.time == 0)
                {
                    return false;
                }
                else if(interacted.time + DELAY_DISCONNECT >= time)
                {
                    // wait...
                }
                else if(interacted.time + DELAY_PUTCABLE >= time)
                {
                    targetFinger = true;
                }
                else
                {
                    interacted.time = 0;
                    return false;
                }
            }
            else if(interacted.time != 0)
            {
                if((interacted.time + DELAY_GRABCABLE) >= time)
                {
                    return true;
                }
                else if((interacted.time + DELAY_CONNECT) >= time)
                {
                    targetFinger = true;
                }
                else
                {
                    interacted.time = 0;
                }
            }

            var targetEnt = MyAPIGateway.Entities.GetEntityById(interacted.target);
            var targetPos = targetEnt.WorldMatrix.Translation;

            if(targetFinger)
            {
                int fingerBone;

                if(charEnt.AnimationController.FindBone(BONE_RIGHTHAND_INDEXTIP, out fingerBone) == null)
                {
                    var obj = charEnt.GetObjectBuilder(false) as MyObjectBuilder_Character;
                    Log.Error("Can't find bone '" + BONE_RIGHTHAND_INDEXTIP + "' on character type '" + obj.CharacterModel + "'");
                    return false;
                }

                var fingerBoneMatrix = charEnt.BoneAbsoluteTransforms[fingerBone];
                targetPos = charEnt.WorldMatrix.Translation + Vector3.TransformNormal(fingerBoneMatrix.Translation, charEnt.WorldMatrix);
            }
            else
            {
                if(interacted.offset.HasValue)
                    targetPos += Vector3.TransformNormal(interacted.offset.Value, targetEnt.WorldMatrix);

                if(targetEnt is MyCubeBlock)
                    targetPos -= Vector3.TransformNormal((targetEnt as MyCubeBlock).BlockDefinition.ModelOffset, targetEnt.WorldMatrix);
            }

            int lcdBone;

            if(charEnt.AnimationController.FindBone(BONE_LEFTARM_LCD, out lcdBone) == null)
            {
                var obj = charEnt.GetObjectBuilder(false) as MyObjectBuilder_Character;
                Log.Error("Can't find bone '" + BONE_LEFTARM_LCD + "' on character type '" + obj.CharacterModel + "'");
                return false;
            }

            var lcdBoneMatrix = charEnt.BoneAbsoluteTransforms[lcdBone];
            var charPos = charEnt.WorldMatrix.Translation + Vector3.TransformNormal(lcdBoneMatrix.Translation, charEnt.WorldMatrix); // + (charEnt.WorldMatrix.Up * 0.05)
            var distSq = Vector3D.DistanceSquared(charPos, targetPos);

            if(distSq > 225) // 15m squared
            {
                interacted.Clear();
                return true;
            }

            if(++interacted.skipGravity >= SKIP_TICKS_GRAVITY)
            {
                interacted.skipGravity = 0;

                var ctrlEnt = (charEnt as VRage.Game.ModAPI.Interfaces.IMyControllableEntity);

                interacted.gravity = (ctrlEnt.EnabledThrusts ? MyParticlesManager.CalculateGravityInPoint(charPos) : charEnt.Physics.Gravity);
                interacted.inGravity = (interacted.gravity.LengthSquared() > 0);
            }

            const int segments = 5;
            double maxLenSq = 0.008;
            maxLenSq *= maxLenSq;

            if(interacted.cable == null)
            {
                interacted.cableAtFingers = targetFinger;
                interacted.cable = new List<CableJoint>();

                for(int i = 0; i < segments; i++)
                {
                    interacted.cable.Add(new CableJoint() { position = Vector3D.Lerp(charPos, targetPos, ((double)i / (double)segments)) });
                }
            }

            int lastIndex = interacted.cable.Count - 1;

            interacted.cable[0].position = charPos;
            interacted.cable[lastIndex].position = targetPos;

            if(interacted.cableAtFingers != targetFinger)
            {
                interacted.cableAtFingers = targetFinger;

                for(int i = 1; i < lastIndex; i++) // recalculate the cable segment positions to avoid it jumping around when changing anchor positions
                {
                    interacted.cable[i].position = Vector3D.Lerp(charPos, targetPos, ((double)i / (double)segments));
                }
            }

            // fake cable physics - please do not take without permission
            for(int i = 1; i < lastIndex; i++)
            {
                var r = interacted.cable[i];
                var p = interacted.cable[i - 1];
                var n = interacted.cable[i + 1];

                if(interacted.inGravity)
                    r.velocity += interacted.gravity / 9.81f;

                Vector3D pDir = (p.position - r.position);
                Vector3D nDir = (n.position - r.position);
                double pDirLen = pDir.LengthSquared();
                double nDirLen = nDir.LengthSquared();

                var dir = pDir * 30 * Math.Min(pDirLen, 1);
                dir += nDir * 30 * Math.Min(nDirLen, 1);
                r.velocity += (dir + (dir - r.prevDir) * 5);
                r.prevDir = dir;
                r.position += (r.velocity / 60.0f) / 10.0f;
            }

            Vector3D p0, p1, p2, p3;
            MyEntity e;

            for(int i = 0; i < lastIndex; i++)
            {
                var r = interacted.cable[i];
                var n = interacted.cable[i + 1];

                p0 = r.position;
                p1 = Vector3D.Lerp(r.position, n.position, 1.0 / 3.0);
                p2 = Vector3D.Lerp(r.position, n.position, 2.0 / 3.0);
                p3 = n.position;

                if(i > 0)
                {
                    var p = interacted.cable[i - 1];
                    var p_p1 = Vector3D.Lerp(r.position, p.position, 1.0 / 3.0);
                    var center = (p_p1 + p1) / 2;
                    p1 = r.position + (p1 - center);
                }

                if(i < (lastIndex - 1))
                {
                    var n2 = interacted.cable[i + 2];
                    var n2_p1 = Vector3D.Lerp(n.position, n2.position, 1.0 / 3.0);
                    var center = (p2 + n2_p1) / 2;
                    p2 = n.position + (p2 - center);
                }

                double step = (1.0 / ((double)settings.cableResolution / (double)segments));
                bool model = settings.cableModel > 0;
                int modelIndex = 0;

                if(model && r.ents == null)
                {
                    r.ents = new List<MyEntity>((int)(1 / step));
                }

                for(double t = step; Math.Round(t, 3) <= 1.0; t += step)
                {
                    var start = GetCurvePointAt(ref p0, ref p1, ref p2, ref p3, t - (step + 0.005));
                    var end = GetCurvePointAt(ref p0, ref p1, ref p2, ref p3, t);

                    if(model)
                    {
                        if(r.ents.Count > modelIndex)
                        {
                            e = r.ents[modelIndex];
                        }
                        else
                        {
                            e = new MyEntity();
                            e.Init(null, pathToMod + @"Models\Cable" + settings.cableModel + ".mwm", targetEnt as MyEntity, null, null);
                            e.Flags = EntityFlags.Visible | EntityFlags.NeedsDraw | EntityFlags.NeedsDrawFromParent | EntityFlags.InvalidateOnMove;
                            e.OnAddedToScene(null);
                            r.ents.Add(e);
                        }

                        Vector3 dir = (start - end);
                        double width = settings.cableWidth * 10;
                        var scale = new Vector3D(width, width, dir.Length() * 10);
                        var matrix = MatrixD.CreateWorld((start + end) / 2, dir, Vector3.Up) * targetEnt.WorldMatrixInvScaled;
                        MatrixD.Rescale(ref matrix, ref scale);
                        e.PositionComp.SetLocalMatrix(matrix);
                        modelIndex++;
                    }
                    else
                    {
                        Vector3 dir = end - start;
                        float len = dir.Normalize();

                        if(len > 0.001f)
                        {
                            float width = (float)settings.cableWidth / 2;
                            MyTransparentGeometry.AddLineBillboard("SquareIgnoreDepth", CABLE_COLOR, start, dir, len, width, 0, false, -1);
                        }
                    }
                }
            }

            return true;
        }

        private void UpdateCharacterReference(IMyEntity ent)
        {
            characterEntity = ent as IMyCharacter;
        }

        private string GetSelectedAreaName() // code converted from Sandbox.Game.Entities.Character.DoDetection(), NOTE: it only fits this mods' needs, it's not fully converted.
        {
            var headMatrix = characterEntity.GetHeadMatrix(false);

            // TODO remove?
            //var headPos = headMatrix.Translation - headMatrix.Forward * 0.3;
            //
            //Vector3D from;
            //Vector3D dir;
            //
            //if(false)
            //{
            //    var cameraMatrix = MyAPIGateway.Session.Camera.WorldMatrix;
            //    dir = cameraMatrix.Forward;
            //    from = MyUtils.LinePlaneIntersection(headPos, (Vector3)dir, cameraMatrix.Translation, (Vector3)dir);
            //}
            //else
            //{
            //    dir = headMatrix.Forward;
            //    from = headPos;
            //}
            //
            //var to = from + dir * MyConstants.DEFAULT_INTERACTIVE_DISTANCE;

            var from = headMatrix.Translation - headMatrix.Forward * 0.3;
            var to = from + headMatrix.Forward * MyConstants.DEFAULT_INTERACTIVE_DISTANCE;

            sphere.Center = from;
            detectableEnts.Clear();
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, detectableEnts, MyEntityQueryType.Both);

            float closestDistance = float.MaxValue;
            IMyEntity closestEntity = null;
            string closestType = null;

            foreach(MyEntity ent in detectableEnts)
            {
                if(!(ent is IMyTerminalBlock)) // ignore non-TerminalBlock entities
                    continue;

                var useObjectComp = ent.Components.Get<MyUseObjectsComponentBase>() as MyUseObjectsComponent;

                if(useObjectComp == null)
                    continue;

                float distance;
                var useObj = useObjectComp.RaycastDetectors(from, to, out distance) as object; // casting to object because IMyUseObject is prohibited, even storing it!

                if(useObj != null && Math.Abs(distance) < Math.Abs(closestDistance))
                {
                    closestDistance = distance;
                    closestEntity = ent;
                    closestType = useObj.ToString();
                }
            }

            if(closestEntity != null)
            {
                hits.Clear();
                MyAPIGateway.Physics.CastRay(from, to, hits); // cast a ray to ensure physics hit

                foreach(var hit in hits)
                {
                    var hitEnt = hit.HitEntity;

                    if(hitEnt is IMyCubeGrid || hitEnt is MyEntitySubpart)
                    {
                        // check if the hit entity is the one with the interaction or its parent (in case of subparts)
                        if(hitEnt.EntityId == closestEntity.EntityId || (hitEnt.Parent != null && hitEnt.Parent.EntityId == closestEntity.EntityId))
                        {
                            return closestType;
                        }

                        // TODO remove?
                        //var grid = (IMyCubeGrid)hitEnt;
                        //var blockPos = grid.RayCastBlocks(hit.Position - dir * 0.1, to);
                        //
                        //if(blockPos.HasValue)
                        //{
                        //    var block = grid.GetCubeBlock(blockPos.Value);
                        //
                        //    if(block.FatBlock is IMyTerminalBlock)
                        //    {
                        //        MyAPIGateway.Utilities.ShowNotification("block=" + block, 16, MyFontEnum.Red); // DEBUG
                        //        return closestType;
                        //    }
                        //}

                        foreach(var c in hitEnt.Hierarchy.Children)
                        {
                            if(c.Entity != null && c.Entity.EntityId == closestEntity.EntityId)
                            {
                                return closestType;
                            }
                        }
                    }
                }
            }

            return null;
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(!init)
                {
                    if(MyAPIGateway.Session == null || MyAPIGateway.Multiplayer == null)
                        return;

                    Init();
                }

                if(isThisHostDedicated)
                    return;

                if(++skipPlanets >= SKIP_TICKS_PLANETS)
                {
                    skipPlanets = 0;

                    planets.Clear();
                    MyAPIGateway.Entities.GetEntities(ents, delegate (IMyEntity e)
                                                      {
                                                          var planet = e as MyPlanet;

                                                          if(planet != null)
                                                          {
                                                              if(!planets.ContainsKey(e.EntityId))
                                                                  planets.Add(e.EntityId, e as MyPlanet);

                                                              return false;
                                                          }

                                                          // TODO remove in a future version
                                                          // resets the new animation system back to true for default character models
                                                          var character = e as IMyCharacter;

                                                          if(character != null)
                                                          {
                                                              var skinned = e as MySkinnedEntity;

                                                              if(character.IsPlayer && skinned != null && !skinned.UseNewAnimationSystem && character.ToString() == "Default_Astronaut")
                                                              {
                                                                  skinned.UseNewAnimationSystem = true;
                                                              }

                                                              return false;
                                                          }

                                                          return false; // no reason to add to the list
                                                      });
                }

                if(settings.cables && interactEntity.Count > 0)
                {
                    foreach(var kv in interactEntity)
                    {
                        if(!InteractLoop(kv.Key, kv.Value))
                        {
                            kv.Value.Clear();
                            removeInteractEntity.Add(kv.Key);
                        }
                    }

                    if(removeInteractEntity.Count > 0)
                    {
                        foreach(var key in removeInteractEntity)
                        {
                            interactEntity.Remove(key);
                        }

                        removeInteractEntity.Clear();
                    }
                }

                var controlled = MyAPIGateway.Session.ControlledObject;

                if(controlled == null || controlled.Entity == null || (characterEntity != null && characterEntity.Closed))
                {
                    UpdateCharacterReference(null);
                }
                else
                {
                    if(controlled is IMyCharacter)
                    {
                        UpdateCharacterReference(controlled.Entity);
                    }
                    else if(controlled is MyShipController)
                    {
                        var shipController = controlled as MyShipController;
                        UpdateCharacterReference(shipController.Pilot);
                    }
                }

                if(characterEntity != null)
                {
                    ulong myId = MyAPIGateway.Multiplayer.MyId;
                    bool inMenu = MyAPIGateway.Gui.GetCurrentScreen != MyTerminalPageEnum.None; // = MyAPIGateway.Gui.ActiveGamePlayScreen != null;

                    if(!inMenu) // TODO remove when MyAPIGateway.Gui.ActiveGamePlayScreen's NullRefException is fixed
                    {
                        try
                        {
                            inMenu = MyAPIGateway.Gui.ActiveGamePlayScreen != null;
                        }
                        catch(Exception)
                        {
                            inMenu = false;
                        }
                    }

                    var skinned = (characterEntity as MySkinnedEntity);
                    var intEnt = (MyAPIGateway.Session.ControlledObject is IMyCharacter ? MyAPIGateway.Gui.InteractedEntity as IMyCubeBlock : null);

                    // button press/holding animations
                    {
                        bool selected = false;

                        if(!inMenu && !(controlled is MyRemoteControl))
                        {
                            var type = GetSelectedAreaName();

                            if(type != null)
                            {
                                selected = true;
                                int index = type.LastIndexOf('.') + 1;

                                if(index > 0 && type.Length > index)
                                {
                                    type = type.Substring(index);

                                    if(useHoldObjects.Contains(type))
                                    {
                                        if(MyAPIGateway.Input.IsGameControlPressed(MyControlsSpace.USE))
                                        {
                                            if(!lastHoldingUse)
                                            {
                                                lastHoldingUse = true;
                                                TriggerInteraction(skinned, InteractionType.USE_HOLD_ON);
                                            }
                                        }
                                        else if(lastHoldingUse)
                                        {
                                            lastHoldingUse = false;
                                            TriggerInteraction(skinned, InteractionType.USE_HOLD_OFF);
                                        }
                                    }
                                    else if(useObjects.Contains(type))
                                    {
                                        long time = DateTime.UtcNow.Ticks;

                                        if(lastInteraction == 0 || lastInteraction <= time)
                                        {
                                            if(MyAPIGateway.Input.IsNewGameControlPressed(MyControlsSpace.USE))
                                            {
                                                lastInteraction = time + interactionEffect[InteractionType.USE_BUTTON].delayTicks;

                                                TriggerInteraction(skinned, InteractionType.USE_BUTTON);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if(lastHoldingUse && !selected)
                        {
                            lastHoldingUse = false;
                            TriggerInteraction(skinned, InteractionType.USE_HOLD_OFF);
                        }
                    }

                    if(inMenu || controlled is MyRemoteControl || MyAPIGateway.Session.CameraController is MyCameraBlock)
                    {
                        var page = MyAPIGateway.Gui.GetCurrentScreen;

                        if(!lcdShown)
                        {
                            lcdShown = true;
                            lastPage = page;

                            if(intEnt != null)
                            {
                                Vector3? offset = null;
                                var block = intEnt as MyCubeBlock;

                                if(block != null)
                                {
                                    //var pos = Sandbox.Game.Gui.MyHud.SelectedObjectHighlight.InteractiveObject.WorldMatrix.Translation; // not allowed in scripts :(
                                    //offset = pos - block.WorldMatrix.Translation;

                                    var view = controlled.GetHeadMatrix(false, true);
                                    var start = view.Translation;
                                    var box = new MyOrientedBoundingBoxD(block.WorldMatrix);
                                    box.Center = block.WorldMatrix.Translation + Vector3.TransformNormal(block.BlockDefinition.ModelOffset, block.WorldMatrix);
                                    box.HalfExtent = block.BlockDefinition.Size * (block.CubeGrid.GridSize / 2) + 0.15f;
                                    var end = start + view.Forward * 6;
                                    IHitInfo hitInfo;

                                    if(MyAPIGateway.Physics.CastRay(start, end, out hitInfo))
                                    {
                                        var hitPos = hitInfo.Position;

                                        if(box.Contains(ref hitPos))
                                        {
                                            offset = Vector3D.TransformNormal(hitPos - block.WorldMatrix.Translation, block.PositionComp.WorldMatrixInvScaled);
                                        }
                                    }
                                }

                                TriggerInteraction(skinned, InteractionType.START_TARGET, intEnt, offset);
                                startTime = DateTime.UtcNow.Ticks + interactionEffect[InteractionType.START_TARGET].delayTicks;
                            }
                            else
                            {
                                TriggerInteraction(skinned, InteractionType.START);
                                startTime = DateTime.UtcNow.Ticks + interactionEffect[InteractionType.START].delayTicks;
                            }
                        }
                        else
                        {
                            long time = DateTime.UtcNow.Ticks;

                            if(startTime == 0 || startTime <= time)
                            {
                                startTime = 0;

                                if(inMenu)
                                {
                                    if(lastInteraction == 0 || lastInteraction <= time)
                                    {
                                        InteractionType type = InteractionType.START;

                                        if(lastInRC)
                                        {
                                            lastInRC = false;
                                            type = InteractionType.RC_OFF;
                                        }
                                        else if(lastInCamera)
                                        {
                                            lastInCamera = false;
                                            type = InteractionType.CAMERA_OFF;
                                        }
                                        else if(lastPage != page)
                                        {
                                            lastPage = page;
                                            type = InteractionType.TAB;
                                        }
                                        else if(MyAPIGateway.Input.IsAnyNewMouseOrJoystickPressed())
                                        {
                                            type = InteractionType.CLICK;
                                        }
                                        else if(MyAPIGateway.Input.IsAnyKeyPress())
                                        {
                                            keys.Clear();
                                            MyAPIGateway.Input.GetPressedKeys(keys);

                                            if(!KeyListEqual(keys, prevKeys))
                                            {
                                                prevKeys = new HashSet<MyKeys>(keys);
                                                var use = MyAPIGateway.Input.GetGameControl(MyControlsSpace.USE);
                                                var terminal = MyAPIGateway.Input.GetGameControl(MyControlsSpace.TERMINAL);

                                                foreach(var k in keys)
                                                {
                                                    if(k == use.GetKeyboardControl() || k == use.GetSecondKeyboardControl() || k == terminal.GetKeyboardControl() || k == terminal.GetSecondKeyboardControl())
                                                        continue;

                                                    if(MyAPIGateway.Input.IsKeyValid(k))
                                                    {
                                                        type = InteractionType.TYPE;
                                                        break;
                                                    }
                                                }
                                            }

                                            keys.Clear();
                                        }

                                        if(type != InteractionType.START)
                                        {
                                            lastInteraction = time + interactionEffect[type].delayTicks;

                                            TriggerInteraction(skinned, type, forceStayOnLastFrame: true);
                                        }
                                    }
                                    else
                                    {
                                        lastPage = page;
                                    }
                                }
                                else if(controlled is MyRemoteControl)
                                {
                                    if(!lastInRC)
                                    {
                                        lastInteraction = time + interactionEffect[InteractionType.RC_ON].delayTicks;
                                        lastInRC = true;

                                        TriggerInteraction(skinned, InteractionType.RC_ON);
                                    }
                                }
                                else if(MyAPIGateway.Session.CameraController is MyCameraBlock)
                                {
                                    if(!lastInCamera)
                                    {
                                        lastInteraction = time + interactionEffect[InteractionType.RC_ON].delayTicks;
                                        lastInCamera = true;

                                        TriggerInteraction(skinned, InteractionType.CAMERA_ON);
                                    }
                                }
                            }
                        }
                    }
                    else if(lcdShown)
                    {
                        lcdShown = false;
                        lastPage = MyTerminalPageEnum.None;
                        lastInteraction = 0;
                        lastInCamera = false;
                        lastInRC = false;

                        InteractionType type = InteractionType.END;

                        if(interactEntity.ContainsKey(characterEntity.EntityId))
                            type = InteractionType.END_TARGET;

                        TriggerInteraction(skinned, type);
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private static bool KeyListEqual(List<MyKeys> keys, HashSet<MyKeys> prevKeys)
        {
            if(keys.Count != prevKeys.Count)
                return false;

            foreach(var k in keys)
            {
                if(!prevKeys.Contains(k))
                    return false;
            }

            return true;
        }

        public void PlaySound(IMyEntity ent, string name)
        {
            var emitter = new MyEntity3DSoundEmitter(ent as MyEntity);
            emitter.CustomVolume = settings.interactionSounds;
            emitter.PlaySingleSound(new MySoundPair(name));
        }

        public static Vector3D GetCurvePointAt(ref Vector3D p0, ref Vector3D p1, ref Vector3D p2, ref Vector3D p3, double t)
        {
            return new Vector3D(
                cubeBezier(p0.X, p1.X, p2.X, p3.X, t),
                cubeBezier(p0.Y, p1.Y, p2.Y, p3.Y, t),
                cubeBezier(p0.Z, p1.Z, p2.Z, p3.Z, t));
        }

        private static double cubeBezier(double p0, double p1, double p2, double p3, double t)
        {
            double rt = 1.0 - t;
            double rtt = rt * t;
            return rt * rt * rt * p0 + 3 * rt * rtt * p1 + 3 * rtt * t * p2 + t * t * t * p3;
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGenerator))]
    public class GravityGeneratorFlat : GravityGeneratorLogic { }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGeneratorSphere))]
    public class GravityGeneratorSphere : GravityGeneratorLogic { }

    public class GravityGeneratorLogic : MyGameLogicComponent
    {
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Entity.NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                var block = Entity as IMyGravityGeneratorBase;

                if(block.CubeGrid.Physics == null)
                    return;

                InteractionMod.instance.gravityGenerators.Add(block.EntityId, block);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void Close()
        {
            try
            {
                InteractionMod.instance.gravityGenerators.Remove(Entity.EntityId);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return Entity.GetObjectBuilder(copy);
        }
    }
}