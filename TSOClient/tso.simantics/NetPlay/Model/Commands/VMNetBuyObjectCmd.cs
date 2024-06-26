﻿using System.Collections.Generic;
using System.IO;
using FSO.LotView.Model;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.Platform;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetBuyObjectCmd : VMNetCommandBodyAbstract
    {
        public uint GUID;
        public short x;
        public short y;
        public sbyte level;
        public Direction dir;
        public bool Verified;

        public PurchaseMode Mode = PurchaseMode.Normal;
        public byte TargetUpgradeLevel;

        private int value = -1;

        private static HashSet<int> RoomieWhiteList = new HashSet<int>()
        {
            12, 13, 14, 15, 16, 17, 18, 19, 20
        };
        private static HashSet<int> BuilderWhiteList = new HashSet<int>()
        {
            12, 13, 14, 15, 16, 17, 18, 19, 20,
            0, 1, 2, 3, 4, 5, 7, 8, 9 //29 is terrain tool
        };

        private VMMultitileGroup CreatedGroup;

        private List<uint> Blacklist = new List<uint>
        {
            0x24C95F99
        };

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var catalog = Content.Content.Get().WorldCatalog;
            var item = catalog.GetItemByGUID(GUID);
            if (!vm.TS1 && (Blacklist.Contains(GUID) || caller == null)) return false;

            //careful here! if the object can't be placed, we have to give the user their money back.
            if (TryPlace(vm, caller))
            {
                if (vm.GlobalLink != null)
                {
                    vm.GlobalLink.RegisterNewObject(vm, CreatedGroup.BaseObject, (short objID, uint pid) =>
                    {
                        vm.SendCommand(new VMNetUpdatePersistStateCmd()
                        {
                            ObjectID = objID,
                            PersistID = pid
                        });
                    });
                }

                //overwrite value

                var objDefinition = CreatedGroup.BaseObject.MasterDefinition ?? CreatedGroup.BaseObject.Object.OBJ;

                if (Mode != PurchaseMode.Donate) CreatedGroup.InitialPrice = (int)value;

                return true;
            }
            else if (vm.GlobalLink != null && item != null)
            {
                vm.GlobalLink.PerformTransaction(vm, false, uint.MaxValue, caller.PersistID, (int)value,
                (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) =>
                {
                    //check if we got the money back? there's really no reason for that to fail
                    //...and we can't exactly do much about it if it were to!
                });
            }
            return false;
        }

        private bool TryPlace(VM vm, VMAvatar caller)
        {
            if (Mode != PurchaseMode.Donate && !vm.PlatformState.CanPlaceNewUserObject(vm)) return false;
            if (Mode == PurchaseMode.Donate && !vm.PlatformState.CanPlaceNewDonatedObject(vm)) return false;

            var group = vm.Context.CreateObjectInstance(GUID, LotTilePos.OUT_OF_WORLD, dir);
            if (group == null) return false;
            group.ChangePosition(new LotTilePos(x, y, level), dir, vm.Context, VMPlaceRequestFlags.UserPlacement);
            group.ExecuteEntryPoint(11, vm.Context); //User Placement
            if (group.Objects.Count == 0) return false;
            if (group.BaseObject.Position == LotTilePos.OUT_OF_WORLD)
            {
                group.Delete(vm.Context);
                return false;
            }

            if (!vm.TS1)
            {
                foreach (var obj in group.Objects)
                {
                    if (obj is VMGameObject) {
                        var state = ((VMTSOObjectState)obj.TSOState);
                        state.OwnerID = caller.PersistID;
                        if (TargetUpgradeLevel > 0)
                        {
                            state.UpgradeLevel = TargetUpgradeLevel;
                            obj.UpdateTuning(vm);
                            VMNetUpgradeCmd.TryReinit(obj, vm, TargetUpgradeLevel);
                        }
                    }
                }
            }
            CreatedGroup = group;

            if (Mode == PurchaseMode.Donate)
            {
                //this object should be donated.
                (CreatedGroup.BaseObject.TSOState as VMTSOObjectState).Donate(vm, CreatedGroup.BaseObject);
            }

            vm.SignalChatEvent(new VMChatEvent(caller, VMChatEventType.Arch,
                caller?.Name ?? "Unknown",
                vm.GetUserIP(caller?.PersistID ?? 0),
                "placed " + group.BaseObject.ToString() + " at (" + x / 16f + ", " + y / 16f + ", " + level + ")"
            ));
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (Verified) return true; //set internally when transaction succeeds. trust that the verification happened.
            value = 0; //do not trust value from net
            if (!vm.TS1)
            {
                Mode = vm.PlatformState.Validator.GetPurchaseMode(Mode, caller, GUID, false);
                if (Mode == PurchaseMode.Disallowed) return false;
                if (Mode == PurchaseMode.Normal && !vm.PlatformState.CanPlaceNewUserObject(vm)) return false;
                if (Mode == PurchaseMode.Donate && !vm.PlatformState.CanPlaceNewDonatedObject(vm)) return false;
            }

            //TODO: error feedback for client
            var catalog = Content.Content.Get().WorldCatalog;
            var objects = Content.Content.Get().WorldObjects;
            var item = catalog.GetItemByGUID(GUID);
            
            if (item != null)
            {
                var price = (int)item.Value.Price;

                if (TargetUpgradeLevel > 0)
                {
                    var obj = objects.Get(GUID);
                    if (obj != null)
                    {
                        var upgradePrice = Content.Content.Get().Upgrades.GetUpgradePrice(obj.Resource.MainIff.Filename, GUID, TargetUpgradeLevel);
                        if (upgradePrice == null) return false; //invalid upgrade level
                        price = upgradePrice.Value;
                    }
                }

                var dcPercent = VMBuildableAreaInfo.GetDiscountFor(item.Value, vm);
                value = (price * (100 - dcPercent)) / 100;
                if (Mode == PurchaseMode.Donate) value -= (value * 2) / 3;
            }

            //TODO: fine grained purchase control based on user status

            //perform the transaction. If it succeeds, requeue the command
            vm.GlobalLink.PerformTransaction(vm, false, caller?.PersistID ?? uint.MaxValue, uint.MaxValue, value,
                (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) =>
                {
                    if (success)
                    {
                        Verified = true;
                        vm.ForwardCommand(this);
                    }
                });

            return false;
        }

        #region VMSerializable Members

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(GUID);
            writer.Write(x);
            writer.Write(y);
            writer.Write(level);
            writer.Write((byte)dir);
            writer.Write(value);

            writer.Write((byte)Mode);
            writer.Write(TargetUpgradeLevel);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            GUID = reader.ReadUInt32();
            x = reader.ReadInt16();
            y = reader.ReadInt16();
            level = reader.ReadSByte();
            dir = (Direction)reader.ReadByte();
            value = reader.ReadInt32();

            Mode = (PurchaseMode)reader.ReadByte();
            TargetUpgradeLevel = reader.ReadByte();
        }

        #endregion
    }
}
