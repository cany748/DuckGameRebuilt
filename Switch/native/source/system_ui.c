#include <switch.h>
#include <string.h>

/* Console features the original Switch release has (DuckGame NSwitch / SwitchSixAxis), on libnx.
 * Used by DuckGame.Switch/Platform/SwitchSystem.cs. */

/* Motion: the original reads one six-axis sensor per controller (the first handle of its style, gyro
 * zero drift mode Standard) and returns angle.y for a single Joy-Con held sideways, angle.x otherwise. */
#define NPAD_SLOTS 9 /* No1-No8 and handheld */
static HidSixAxisSensorHandle sixaxis_handle[NPAD_SLOTS];
static u32 sixaxis_style[NPAD_SLOTS];

float switch_sixaxis_angle(int index)
{
    if (index < 0 || index > 7)
        return 0;
    HidNpadIdType id = (HidNpadIdType)index;
    int slot = index;
    u32 style = hidGetNpadStyleSet(id) & 0x1f;
    if (index == 0 && style == 0)
    {
        id = HidNpadIdType_Handheld;
        slot = 8;
        style = hidGetNpadStyleSet(id) & 0x1f;
    }
    if (style == 0)
        return 0;
    if (sixaxis_style[slot] != style)
    {
        HidNpadStyleTag tag = (HidNpadStyleTag)(style & -style);
        HidSixAxisSensorHandle handles[2];
        int count = tag == HidNpadStyleTag_NpadJoyDual || tag == HidNpadStyleTag_NpadHandheld ? 2 : 1;
        if (R_FAILED(hidGetSixAxisSensorHandles(handles, count, id, tag)))
            return 0;
        hidStartSixAxisSensor(handles[0]);
        hidSetGyroscopeZeroDriftMode(handles[0], HidGyroscopeZeroDriftMode_Standard);
        sixaxis_handle[slot] = handles[0];
        sixaxis_style[slot] = style;
    }
    HidSixAxisSensorState state;
    if (hidGetSixAxisSensorStates(sixaxis_handle[slot], &state, 1) == 0)
        return 0;
    return style & (HidNpadStyleTag_NpadJoyLeft | HidNpadStyleTag_NpadJoyRight) ? state.angle.y : state.angle.x;
}

/* System keyboard; blocks until it closes. Returns 1 with out filled, 0 when cancelled, -1 when it can't be shown. */
int switch_keyboard(const char *header, const char *initial, int max_length, int filename, char *out, int out_size)
{
    SwkbdConfig keyboard;
    if (R_FAILED(swkbdCreate(&keyboard, 0)))
        return -1;
    swkbdConfigMakePresetDefault(&keyboard);
    swkbdConfigSetHeaderText(&keyboard, header);
    swkbdConfigSetInitialText(&keyboard, initial);
    if (max_length > 0)
        swkbdConfigSetStringLenMax(&keyboard, (u32)max_length);
    if (filename)
        swkbdConfigSetKeySetDisableBitmask(&keyboard, SwkbdKeyDisableBitmask_ForwardSlash | SwkbdKeyDisableBitmask_Backslash);
    out[0] = 0;
    Result result = swkbdShow(&keyboard, out, (size_t)out_size);
    swkbdClose(&keyboard);
    return R_SUCCEEDED(result);
}

/* The system's controller pairing screen ("SET CONTROLLERS" in the original's options). */
void switch_controller_config(int max_players)
{
    HidLaControllerSupportArg arg;
    hidLaCreateControllerSupportArg(&arg);
    arg.hdr.player_count_min = 0;
    arg.hdr.player_count_max = (s8)max_players;
    arg.hdr.enable_take_over_connection = 1;
    arg.hdr.enable_permit_joy_dual = 1;
    HidLaControllerSupportResultInfo info;
    hidLaShowControllerSupport(&info, &arg);
}

/* While on, pressing L+R or SL+SR on Joy-Cons joins or splits them (the original turns it on in the title
 * screen and the lobby, and off when a match starts). */
void switch_lr_assignment(int on)
{
    if (on)
        hidStartLrAssignmentMode();
    else
        hidStopLrAssignmentMode();
}

/* Nickname of the user the game runs as (or the last one opened). Returns 1 with out filled. */
int switch_account_name(char *out, int out_size)
{
    out[0] = 0;
    if (R_FAILED(accountInitialize(AccountServiceType_Application)))
        return 0;
    AccountUid uid;
    int found = R_SUCCEEDED(accountGetPreselectedUser(&uid)) && accountUidIsValid(&uid);
    if (!found)
        found = R_SUCCEEDED(accountGetLastOpenedUser(&uid)) && accountUidIsValid(&uid);
    if (found)
    {
        AccountProfile profile;
        found = R_SUCCEEDED(accountGetProfile(&profile, uid));
        if (found)
        {
            AccountProfileBase base;
            found = R_SUCCEEDED(accountProfileGet(&profile, NULL, &base));
            if (found)
            {
                strncpy(out, base.nickname, (size_t)out_size - 1);
                out[out_size - 1] = 0;
            }
            accountProfileClose(&profile);
        }
    }
    accountExit();
    return found && out[0] != 0;
}
