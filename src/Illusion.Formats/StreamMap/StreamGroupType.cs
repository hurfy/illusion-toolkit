namespace Illusion.Formats.StreamMap;

/// <summary>
/// Mafia II stream-group types (engine values, with gaps). They define what a loader's
/// asset is: geometry (City*, Char*, Car*, Weapons…) or service data (Script/Sound/GUI…).
/// Copied 1:1 from the toolkit's <c>ResourceTypes.Misc.GroupTypes</c>.
/// </summary>
public enum StreamGroupType
{
    Null = 0, City = 1, City_Ground = 2, City_Univers = 3, Shop = 4, Char_Universe = 5,
    Player = 6, H_Char = 7, L_Char = 8, Police_Char = 9, Car_Universe = 10, Car = 11,
    Car_Big = 12, Car_Police = 13, Car_Script = 14, Base_Anim = 15, Weapons = 16, GUI = 17,
    Sky = 18, Tables = 19, Default_Sound = 20, Particles = 21, Game_Script = 23,
    Mission_Script = 24, Script = 25, TwoH_Char_In_One = 26, FiveL_Char_In_One = 27,
    ThreeCar_In_One = 28, Char_Police = 29, Trees = 30, City_Crash = 31, Small = 33,
    Generate = 32, Script_Sounds = 34, Director_Lua = 35, Mapa = 36, Sound_City = 37,
    Anims_City = 38, Kyn_City_Part = 39, Kyn_City_Shop = 40, Generic_Speech_Normal = 41,
    Generic_Speech_Gangster = 42, Generic_Speeh_Various = 43, Generic_Speech_Story = 44,
    Big_Script = 45, Big_Mission_Script = 46, Generic_Speech_Police = 47, Text = 48,
    Ingame = 50, Ingame_GUI = 51, Dabing = 52,
}
