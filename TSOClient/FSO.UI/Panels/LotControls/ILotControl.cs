﻿using FSO.LotView;
using FSO.LotView.Model;
using FSO.SimAntics;

namespace FSO.UI.Panels.LotControls
{
    public interface ILotControl
    {
        VMEntity ActiveEntity { get; }
        World World { get; }
        int Budget { get; }
        I3DRotate Rotate { get; }
    }
}
