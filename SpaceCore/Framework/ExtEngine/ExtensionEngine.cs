using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Miniscript;
using SpaceCore.Framework.ExtEngine.Models;
using SpaceCore.Framework.ExtEngine.Script;
using SpaceShared;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace SpaceCore.Framework.ExtEngine
{
    internal static partial class ExtensionEngine
    {
        // content patcher stuff
        private static IMod contentPatcher;
        private static Func<object> screenManager;
        private static PropertyInfo screenManVal;
        private static PropertyInfo tokenManProp;
        private static Func<object> tokenManager;
        private static object logPathBuilder;
        private static Dictionary<string, object> contexts = new();
        private static MethodInfo trackLocalFunc;
        private static ConstructorInfo tokenStringConstructor;
        private static PropertyInfo tokenStringIsReadyProp;
        private static PropertyInfo tokenStringValueProp;

        public static void Init()
        {
            SpaceCore.Instance.Helper.Events.Content.AssetRequested += OnAssetRequested;

            SpaceCore.Instance.Helper.ConsoleCommands.Add("ext_ui", "...", OnExtUi);

            SpaceCore.Instance.Helper.Events.GameLoop.UpdateTicked += GameLoop_UpdateTicked;
        }

        private static int countdown = 3;
        private static void GameLoop_UpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (--countdown <= 0)
            {
                SpaceCore.Instance.Helper.Events.GameLoop.UpdateTicked -= GameLoop_UpdateTicked;

                var modInfo = SpaceCore.Instance.Helper.ModRegistry.Get("Pathoschild.ContentPatcher");
                contentPatcher = modInfo.GetType().GetProperty("Mod", BindingFlags.Public | BindingFlags.Instance).GetValue(modInfo) as IMod;
                object smPerScreen = contentPatcher.GetType().GetField("ScreenManager", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(contentPatcher);
                screenManVal = smPerScreen.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                screenManager = () => screenManVal.GetGetMethod().Invoke(smPerScreen, new object[0]);
                tokenManProp = screenManager().GetType().GetProperty("TokenManager");
                tokenManager = () => tokenManProp.GetGetMethod().Invoke(screenManager(), new object[0]);
                logPathBuilder = AccessTools.TypeByName("ContentPatcher.Framework.LogPathBuilder").GetConstructor(new Type[] { typeof(string[]) }).Invoke(new object[] { new string[] { "SpaceCore shenanigans" } });
                trackLocalFunc = tokenManager().GetType().GetMethod("TrackLocalTokens");
                var tokenStringType = AccessTools.TypeByName("ContentPatcher.Framework.Conditions.TokenString");
                tokenStringConstructor = tokenStringType.GetConstructor(new Type[] { typeof(string), AccessTools.TypeByName("ContentPatcher.Framework.Tokens.IContext"), AccessTools.TypeByName("ContentPatcher.Framework.LogPathBuilder") });
                tokenStringIsReadyProp = tokenStringType.GetProperty("IsReady");
                tokenStringValueProp = tokenStringType.GetProperty("Value");
            }
        }

        public static bool CheckWhen(string contentPack, string when)
        {
            // TODO: Properly use CP's ManagedCondition stuff for this
            string whenSubstituted = SubstituteTokens(contentPack, when);
            using DataTable dt = new();
            object result = dt.Compute(whenSubstituted, string.Empty);
            if (result is not bool)
            {
                Log.Warn($"In {contentPack}, {when} should return true or false!" );
                return false;
            }
            return (bool) result;
        }

        public static string SubstituteTokens(string contentPack, string text)
        {
            if (contentPatcher == null)
            {
                Log.Warn("Content Patcher not found!");
                return text;
            }
            if (!contexts.ContainsKey(contentPack))
            {
                var modInfo = SpaceCore.Instance.Helper.ModRegistry.Get(contentPack);
                var cp = modInfo.GetType().GetProperty( "ContentPack", BindingFlags.Public | BindingFlags.Instance ).GetValue( modInfo ) as IContentPack;
                object newContext = trackLocalFunc.Invoke(tokenManager(), new object[] { cp });
                contexts.Add(contentPack, newContext);
                return SubstituteTokens(contentPack, text);
            }

            object context = contexts[contentPack];
            object tokenStr = tokenStringConstructor.Invoke(new object[] { text, context, logPathBuilder });
            if ((bool)tokenStringIsReadyProp.GetGetMethod().Invoke(tokenStr, null) == false)
            {
                throw new Exception("Tokens not ready!");
            }
            return (string) tokenStringValueProp.GetGetMethod().Invoke(tokenStr, null);
        }

        private static void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo("spacechase0.SpaceCore/UI"))
                e.LoadFrom(() => new Dictionary<string, UiContentModel>(), AssetLoadPriority.Exclusive);
        }

        private static void OnExtUi(string cmd, string[] args)
        {
            if (args.Length != 1)
            {
                Log.Info("Bad arguments");
                return;
            }

            var data = Game1.content.Load<Dictionary<string, UiContentModel>>("spacechase0.SpaceCore/UI");
            if (!data.ContainsKey(args[0]))
            {
                Log.Info("Bad ID");
                return;
            }
            Game1.activeClickableMenu = new ExtensionMenu(data[args[0]]);
        }

        internal static Func<Item, Value> makeItemMap;
        internal static Func<Farmer, Value> makeFarmerMap;
        internal static Func<GameLocation, Value> makeLocationMap;

        public static Interpreter SetupInterpreter()
        {
            Interpreter ret = new();
            ret.standardOutput = (s) => Log.Debug($"Script output: {s}");
            //ret.implicitOutput = (s) => Log.Trace($"Script output: {s}");
            ret.errorOutput = (s) => Log.Error($"Script error: {s}");

            var i = Intrinsic.Create("substituteTokens");
            i.AddParam("text");
            i.code = (ctx, prevResult) =>
            {
                string text = ctx.GetVar("text").ToString();
                var menu = ctx.interpreter.hostData as ExtensionMenu;

                return new Intrinsic.Result(new ValString(SubstituteTokens(menu.origModel.ScriptFile.Substring(0, menu.origModel.ScriptFile.IndexOf('/')), text)));
            };

            i = Intrinsic.Create("hasMail");
            i.AddParam("mail");
            i.AddParam("player", new ValString("current"));
            i.code = (ctx, prevResult) =>
            {
                string mail = ctx.GetVar("mail").ToString();
                var playerVar = ctx.GetVar("player");
                var menu = ctx.interpreter.hostData as ExtensionMenu;

                Farmer player = null;
                if (playerVar is ValString && playerVar.ToString() == "current")
                    player = Game1.player;
                else if (playerVar is ValString && playerVar.ToString() == "master")
                    player = Game1.MasterPlayer;
                else if (playerVar is ValNumber vnum)
                    player = Game1.getFarmerMaybeOffline((long)vnum.value);

                if (player == null)
                {
                    Log.Warn($"Bad player ID ({playerVar}) passed to hasMail by {menu.origModel.ScriptFile}");
                    return new Intrinsic.Result(new ValNumber(0));
                }

                return new Intrinsic.Result(new ValNumber(player.hasOrWillReceiveMail( mail ) ? 1 : 0));
            };

            i = Intrinsic.Create("playSound");
            i.AddParam("cue");
            i.code = (ctx, prevResult) =>
            {
                Game1.playSound(ctx.GetVar("cue").ToString());
                return Intrinsic.Result.Null;
            };

            i = Intrinsic.Create("openMenu");
            i.AddParam("id");
            i.AddParam("asChildMenu", new ValNumber(0));
            i.code = (ctx, prevResult) =>
            {
                string id = ctx.GetVar("id").ToString();
                bool asChild = ctx.GetVar("asChildMenu").BoolValue();
                var menu = ctx.interpreter.hostData as ExtensionMenu;

                var data = Game1.content.Load<Dictionary<string, UiContentModel>>("spacechase0.SpaceCore/UI");
                if (!data.ContainsKey(id))
                {
                    Log.Warn($"In {menu.origModel.ScriptFile}, tried to open menu {id} which does not exist");
                    return Intrinsic.Result.Null;
                }
                var newMenu = new ExtensionMenu(data[id]);
                if (asChild && Game1.activeClickableMenu != null)
                    Game1.activeClickableMenu.SetChildMenu(newMenu);
                else
                    Game1.activeClickableMenu = newMenu;
                return Intrinsic.Result.Null;
            };

            i = Intrinsic.Create("openLetterMenu");
            i.AddParam("text");
            i.AddParam("asChildMenu", new ValNumber(0));
            i.code = (ctx, prevResult) =>
            {
                string text = ctx.GetVar("text").ToString();
                bool asChild = ctx.GetVar("asChildMenu").BoolValue();

                var newMenu = new LetterViewerMenu(text);
                if (asChild && Game1.activeClickableMenu != null)
                    Game1.activeClickableMenu.SetChildMenu(newMenu);
                else
                    Game1.activeClickableMenu = newMenu;
                return Intrinsic.Result.Null;
            };

            i = Intrinsic.Create("createTemporarySprite");
            i.AddParam("position");
            i.AddParam("texturePath");
            i.AddParam("sourceRect");
            i.AddParam("lifetime");
            i.code = (ctx, prevResult) =>
            {
                Vector2 pos = (ctx.GetVar("position") as ValMap).ToVector2();
                string texPath = ctx.GetVar("texturePath").ToString();
                Rectangle src = (ctx.GetVar("sourceRect") as ValMap).ToRectangle();

                TemporaryAnimatedSprite tas = null;
                try
                {
                    float animInterval = ctx.GetVar("animationInterval").FloatValue();
                    int animLength = ctx.GetVar("animationLength").IntValue();
                    int animLoops = ctx.GetVar("animationLoopCount").IntValue();

                    tas = new TemporaryAnimatedSprite(texPath, src, animInterval, animLength, animLoops, pos, false, false, -1f, 0, Color.White, 1, 0, 0, 0);
                }
                catch (UndefinedIdentifierException e)
                {
                    float lifetime = ctx.GetVar("lifetime").FloatValue();
                    tas = new TemporaryAnimatedSprite(texPath, src, pos, false, 0, Color.White);
                    tas.interval = lifetime;
                }

                ValMap retMap = new();
                retMap.map.Add(new ValString("__tas"), new ValTAS(tas));

                retMap.assignOverride = (key, val) =>
                {
                    switch (key.ToString())
                    {
                        case "depth":
                            tas.layerDepth = val.FloatValue();
                            return true;
                        case "color":
                            Log.Debug("TODO TAS Color");
                            return true;
                        case "alpha":
                            tas.alpha = val.FloatValue();
                            return true;
                        case "alphaFade":
                            tas.alphaFade = val.FloatValue();
                            return true;
                        case "alphaFadeFade":
                            tas.alphaFadeFade = val.FloatValue();
                            return true;
                        case "scale":
                            tas.scale = val.FloatValue();
                            return true;
                        case "scaleChange":
                            tas.scaleChange = val.FloatValue();
                            return true;
                        case "scaleChangeChange":
                            tas.scaleChangeChange = val.FloatValue();
                            return true;
                        case "rotation":
                            tas.rotation = val.FloatValue();
                            return true;
                        case "rotationChange":
                            tas.rotationChange = val.FloatValue();
                            return true;
                        case "delayBeforeStart":
                            tas.delayBeforeAnimationStart = val.IntValue();
                            return true;
                        case "velocity":
                            tas.motion = (val as ValMap).ToVector2();
                            return true;
                        case "acceleration":
                            tas.acceleration = (val as ValMap).ToVector2();
                            return true;
                        case "accelerationChange":
                            tas.accelerationChange = (val as ValMap).ToVector2();
                            return true;
                        case "shakeIntensity":
                            tas.shakeIntensity = val.FloatValue();
                            return true;
                        case "shakeIntensityChange":
                            tas.shakeIntensityChange = val.FloatValue();
                            return true;
                        case "xPeriodicRange":
                            tas.xPeriodic = true;
                            tas.xPeriodicRange = val.FloatValue();
                            return true;
                        case "xPeriodicLoopTime":
                            tas.xPeriodic = true;
                            tas.xPeriodicLoopTime = val.FloatValue();
                            return true;
                        case "yPeriodicRange":
                            tas.yPeriodic = true;
                            tas.yPeriodicRange = val.FloatValue();
                            return true;
                        case "yPeriodicLoopTime":
                            tas.yPeriodic = true;
                            tas.yPeriodicLoopTime = val.FloatValue();
                            return true;
                        case "pulseTime":
                            tas.pulse = true;
                            tas.pulseTime = val.FloatValue();
                            return true;
                        case "pulseAmount":
                            tas.pulse = true;
                            tas.pulseAmount = val.FloatValue();
                            return true;
                        case "xStop":
                            tas.xStopCoordinate = val.IntValue();
                            return true;
                        case "yStop":
                            tas.yStopCoordinate = val.IntValue();
                            return true;
                    }

                    return true;
                };

                return new Intrinsic.Result(retMap);
            };

            var makeTs = i.code;
            i = Intrinsic.Create("createTemporaryAnimatedSprite");
            i.AddParam("position");
            i.AddParam("texturePath");
            i.AddParam("sourceRect");
            i.AddParam("animationInterval");
            i.AddParam("animationLength");
            i.AddParam("animationLoopCount");
            i.code = makeTs;

            i = Intrinsic.Create("broadcastTemporarySprite");
            i.AddParam("location");
            i.AddParam("temporarySprite");
            i.code = (ctx, prevResult) =>
            {
                GameLocation loc = ((ctx.GetVar("location") as ValMap).map[ new ValString( "__location" ) ] as ValGameLocation).location;
                TemporaryAnimatedSprite tas = ((ctx.GetVar("temporarySprite") as ValMap).map[new ValString("__tas")] as ValTAS).tas;

                Game1.Multiplayer.broadcastSprites(loc, tas);

                return Intrinsic.Result.Null;
            };

            i = Intrinsic.Create("createItem");
            i.AddParam("qualifiedItemId");
            i.AddParam("stack", new ValNumber(1));
            i.code = (ctx, prevResult) =>
            {
                Item item = ItemRegistry.Create((ctx.GetVar("qualifiedItemId") as ValString).value, (ctx.GetVar("stack") as ValNumber).IntValue());
                return new(makeItemMap(item));
            };

            makeItemMap = (Item item) =>
            {
                if (item == null)
                    return ValNull.instance;

                ValMap ret = new();
                ret.map.Add(new ValString("__item"), new ValItem(item));
                ret.map.Add(new ValString("itemId"), new ValString(item.ItemId));
                ret.map.Add(new ValString("qualifiedItemId"), new ValString(item.QualifiedItemId));
                ret.map.Add(new ValString("typeDefinitionId"), new ValString(item.TypeDefinitionId));
                ret.map.Add(new ValString("stack"), new ValNumber(item.Stack));

                // TODO: Script data? Or just modData?

                // TODO: Stuff based on what it is (quality, etc.)...
                // Use reflection to do automatically?

                ret.assignOverride = (key, val) =>
                {
                    switch (key.ToString())
                    {
                        case "itemId":
                            item.ItemId = val.ToString();
                            ret.map[key] = val;
                            ret["qualifiedItemId"] = new ValString(item.QualifiedItemId);
                            return true;
                        case "qualifiedItemId": return false;
                        case "typeDefinitionId": return false;
                        case "stack":
                            item.Stack = val.IntValue();
                            ret.map[key] = val;
                            return true;
                    }

                    return true;
                };

                return ret;
            };

            makeFarmerMap = (Farmer farmer) =>
            {
                ValMap ret = new();
                ret.map.Add(new ValString("__farmer"), new ValFarmer(farmer));
                ret.map.Add(new ValString("x"), new ValNumber(farmer.Position.X));
                ret.map.Add(new ValString("y"), new ValNumber(farmer.Position.Y));
                ret.map.Add(new ValString("name"), new ValString(farmer.Name));
                ret.map.Add(new ValString("facing"), new ValNumber(farmer.FacingDirection));
                ret.map.Add(new ValString("canMove"), new ValNumber(farmer.CanMove ? 1 : 0));
                ret.map.Add(new ValString("currentLocation"), makeLocationMap(farmer.currentLocation));

                ret.assignOverride = (key, val) =>
                {
                    switch (key.ToString())
                    {
                        case "x":
                            farmer.Position = new(val.FloatValue(), farmer.Position.Y);
                            ret.map[key] = val;
                            return true;
                        case "y":
                            farmer.Position = new(farmer.Position.X, val.FloatValue());
                            ret.map[key] = val;
                            return true;
                        case "name": return false;
                        case "facing":
                            farmer.FacingDirection = val.IntValue();
                            ret.map[key] = val;
                            return true;
                        case "canMove":
                            farmer.canMove = val.BoolValue();
                            ret.map[key] = val;
                            return true;
                        case "currentLocation":
                            Log.Warn("TODO: set farmer current location");
                            // change the return later too
                            return false;
                    }
                    return true;
                };

                return ret;
            };

            var dropItemFunc = Intrinsic.Create("__dropItem");
            dropItemFunc.AddParam("item");
            dropItemFunc.AddParam("x");
            dropItemFunc.AddParam("y");
            dropItemFunc.code = (ctx, prevResult) =>
            {
                var map = ctx.self as ValMap;
                var loc = (map.map[new ValString("__location")] as ValGameLocation).location;

                var item = (ctx.GetVar("item") as ValMap).map[new ValString("__item")] as ValItem;
                var pos = new Vector2((ctx.GetVar("x") as ValNumber).FloatValue(), (ctx.GetVar("y") as ValNumber).FloatValue());

                loc.debris.Add(new Debris(item.item, pos));

                return Intrinsic.Result.Null;
            };

            makeLocationMap = (GameLocation location) =>
            {
                ValMap ret = new();
                ret.map.Add(new ValString("__location"), new ValGameLocation(location));
                ret.map.Add(new ValString("name"), new ValString(location.Name));
                ret.map.Add(new ValString("uniqueName"), new ValString(location.NameOrUniqueName));
                ret.map.Add(new ValString("dropItem"), dropItemFunc.GetFunc());

                ret.assignOverride = (key, val) =>
                {
                    switch (key.ToString())
                    {
                        case "name": return false;
                        case "uniqueName": return false;
                        case "dropItem": return false;
                    }
                    return true;
                };

                return ret;
            };

            ret.SetGlobalValue("isMasterGame", new ValNumber(Game1.IsMasterGame ? 1 : 0));
            ret.SetGlobalValue("isMultiplayer", new ValNumber(Context.IsMultiplayer ? 1 : 0));

            ret.SetGlobalValue("player", makeFarmerMap(Game1.player));
            ret.SetGlobalValue("currentLocation", makeLocationMap(Game1.currentLocation));

            SetupInterpreter_ExtensionMenu( ret );

            return ret;
        }
    }
}