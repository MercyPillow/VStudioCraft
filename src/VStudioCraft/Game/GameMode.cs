namespace VStudioCraft.Game
{
    // Alpha 1.1.2_01 only really had survival (the creative "classic" flavour was a
    // separate download at the time). We expose both so the player can pick between
    // the existing creative-lite loop and a damage-taking survival loop that's the
    // foundation for a full Alpha-style game.
    internal enum GameMode : byte
    {
        Creative = 0,
        Survival = 1,
    }
}
