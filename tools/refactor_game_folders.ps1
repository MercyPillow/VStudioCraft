#requires -Version 5.0
# Refactor src/VStudioCraft/Game/ into per-domain subfolders so the
# 89-file flat directory becomes navigable. Namespaces stay at
# `VStudioCraft.Game` regardless of subfolder so no source change is
# required — this is purely a filesystem reorganisation.

$ErrorActionPreference = 'Stop'
$gameRoot = "C:\Users\danla\source\repos\VStudioCraft\src\VStudioCraft\Game"

# File → subfolder map. New folders get created lazily.
$mapping = @{
    # Audio/
    'AudioEngine.cs'              = 'Audio'
    'SfxBank.cs'                  = 'Audio'

    # Blocks/
    'Block.cs'                    = 'Blocks'
    'BlockMaterial.cs'            = 'Blocks'
    'BlockTextures.cs'            = 'Blocks'
    'AlphaTerrainData.cs'         = 'Blocks'
    'AlphaToolsData.cs'           = 'Blocks'

    # Entities/
    'Entity.cs'                   = 'Entities'
    'ArrowProjectile.cs'          = 'Entities'
    'ThrownProjectile.cs'         = 'Entities'
    'FireballProjectile.cs'       = 'Entities'
    'Bobber.cs'                   = 'Entities'
    'Boat.cs'                     = 'Entities'
    'Minecart.cs'                 = 'Entities'
    'FallingBlockEntity.cs'       = 'Entities'
    'PrimedTntEntity.cs'          = 'Entities'
    'Painting.cs'                 = 'Entities'
    'DroppedItem.cs'              = 'Entities'

    # Input/
    'InputState.cs'               = 'Input'
    'KeyBindings.cs'              = 'Input'

    # Items/
    'ItemStack.cs'                = 'Items'
    'Inventory.cs'                = 'Items'
    'CraftingRecipes.cs'          = 'Items'
    'FurnaceRecipes.cs'           = 'Items'
    'CreativeCatalog.cs'          = 'Items'

    # Mobs/
    'PassiveMob.cs'               = 'Mobs'
    'PassiveMobs.cs'              = 'Mobs'
    'Pig.cs'                      = 'Mobs'
    'HostileMob.cs'               = 'Mobs'
    'HostileMobs.cs'              = 'Mobs'

    # Multiplayer/
    'RemotePlayer.cs'             = 'Multiplayer'
    'EntityInterpState.cs'        = 'Multiplayer'

    # Player/
    'Player.cs'                   = 'Player'

    # Redstone/
    'RedstonePowerSystem.cs'      = 'Redstone'

    # Rendering/
    'GameRenderer.cs'             = 'Rendering'
    'Camera.cs'                   = 'Rendering'
    'Frustum.cs'                  = 'Rendering'
    'Mesh.cs'                     = 'Rendering'
    'OverlayMesh.cs'              = 'Rendering'
    'ParticleSystem.cs'           = 'Rendering'
    'Shader.cs'                   = 'Rendering'
    'SkyRenderer.cs'              = 'Rendering'
    'SkyTextures.cs'              = 'Rendering'
    'SkinCuboidMesh.cs'           = 'Rendering'
    'SteveSkin.cs'                = 'Rendering'
    'SteveSkinData.cs'            = 'Rendering'
    'TexturedCubeMesh.cs'         = 'Rendering'
    'CrackTextures.cs'            = 'Rendering'

    # Settings/
    'Settings.cs'                 = 'Settings'
    'GameMode.cs'                 = 'Settings'

    # TileEntities/
    'ChestTileEntity.cs'          = 'TileEntities'
    'FurnaceTileEntity.cs'        = 'TileEntities'
    'DispenserTileEntity.cs'      = 'TileEntities'
    'JukeboxTileEntity.cs'        = 'TileEntities'
    'SignTileEntity.cs'           = 'TileEntities'

    # UI/
    'TitleScreen.cs'              = 'UI'
    'WorldSelectScreen.cs'        = 'UI'
    'WorldCreateScreen.cs'        = 'UI'
    'MultiplayerConnectScreen.cs' = 'UI'
    'PauseMenu.cs'                = 'UI'
    'OptionsMenu.cs'              = 'UI'
    'ControlsMenu.cs'             = 'UI'
    'DeathScreen.cs'              = 'UI'
    'LoadingScreen.cs'            = 'UI'
    'InventoryScreen.cs'          = 'UI'
    'CraftingScreen.cs'           = 'UI'
    'FurnaceScreen.cs'            = 'UI'
    'ChestScreen.cs'              = 'UI'
    'DispenserScreen.cs'          = 'UI'
    'HotbarLayout.cs'             = 'UI'
    'HotbarTextures.cs'           = 'UI'
    'HudTextures.cs'              = 'UI'
    'AnimatedBackground.cs'       = 'UI'
    'UiScale.cs'                  = 'UI'

    # World/
    'World.cs'                    = 'World'
    'Chunk.cs'                    = 'World'
    'ChunkJobSystem.cs'           = 'World'
    'ChunkMesher.cs'              = 'World'
    'LightCalculator.cs'          = 'World'
    'Noise.cs'                    = 'World'
    'Biome.cs'                    = 'World'
    'Dimension.cs'                = 'World'
    'WeatherSystem.cs'            = 'World'
    'WorldSaveFormat.cs'          = 'World'
    'FluidTick.cs'                = 'World'
    'Raycast.cs'                  = 'World'
    'TerrainGenerator.cs'         = 'World'
    'NetherTerrainGenerator.cs'   = 'World'
}

# Verify every file in the folder is mapped (so no orphans).
$existing = Get-ChildItem $gameRoot -File -Filter '*.cs' | ForEach-Object { $_.Name }
$unmapped = @()
foreach ($f in $existing) {
    if (-not $mapping.ContainsKey($f)) { $unmapped += $f }
}
if ($unmapped.Count -gt 0) {
    Write-Output "UNMAPPED FILES (will not be moved):"
    foreach ($u in $unmapped) { Write-Output "  $u" }
}
$missing = @()
foreach ($f in $mapping.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $gameRoot $f))) { $missing += $f }
}
if ($missing.Count -gt 0) {
    Write-Output "MAPPED-BUT-MISSING (will be skipped):"
    foreach ($m in $missing) { Write-Output "  $m" }
}

# Create folders + move files.
$folders = $mapping.Values | Sort-Object -Unique
foreach ($folder in $folders) {
    $dst = Join-Path $gameRoot $folder
    if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst | Out-Null }
}

$moved = 0
foreach ($file in $mapping.Keys) {
    $src = Join-Path $gameRoot $file
    if (-not (Test-Path -LiteralPath $src)) { continue }
    $dst = Join-Path (Join-Path $gameRoot $mapping[$file]) $file
    Move-Item -LiteralPath $src -Destination $dst -Force
    $moved++
}
Write-Output ("Moved " + $moved + " files into " + $folders.Count + " folders.")
