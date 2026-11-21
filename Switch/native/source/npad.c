#include <switch.h>

/* How a controller is held, for the game's button handling and icons (DuckGame XInputPad, SWITCH):
 * 0 unknown, 1 Pro Controller, 2 Joy-Con pair or handheld, 3 single left Joy-Con, 4 single right Joy-Con.
 * SDL's device index is the npad id; SDL's handheld controller is the first one, like npad No1. */
int switch_npad_style(int index)
{
    if (index < 0 || index > 7)
        return 0;
    u32 style = hidGetNpadStyleSet((HidNpadIdType)index);
    if (index == 0 && style == 0)
        style = hidGetNpadStyleSet(HidNpadIdType_Handheld);
    if (style & HidNpadStyleTag_NpadFullKey)
        return 1;
    if (style & (HidNpadStyleTag_NpadJoyDual | HidNpadStyleTag_NpadHandheld))
        return 2;
    u32 type = hidGetNpadDeviceType((HidNpadIdType)index);
    if (type == HidDeviceTypeBits_JoyLeft)
        return 3;
    if (type == HidDeviceTypeBits_JoyRight)
        return 4;
    return 0;
}
