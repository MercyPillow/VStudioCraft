# VStudioCraft

A Minecraft Alpha 1.1.2_01-style voxel game built as a Visual Studio extension, rendered directly in a VS tool window using OpenGL 3.3 Core.

![.NET Framework 4.7.2](https://img.shields.io/badge/.NET%20Framework-4.7.2-blue)
![OpenGL 3.3](https://img.shields.io/badge/OpenGL-3.3%20Core-green)
![Visual Studio 2022+](https://img.shields.io/badge/Visual%20Studio-2022%2B-purple)
![License](https://img.shields.io/badge/license-MIT-green)

> **✨ Mine blocks, fight mobs, and explore procedurally generated worlds—all without leaving Visual Studio.**

## 📋 Table of Contents

- [Overview](#overview)
- [Quick Start](#quick-start)
- [Features](#features)
- [Getting Started](#getting-started)
- [Controls](#controls)
- [Block Types](#block-types)
- [World Format](#world-format)
- [Architecture](#architecture)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

## Overview

VStudioCraft brings a fully playable Minecraft-inspired voxel sandbox into Visual Studio as a dockable tool window. Built with OpenGL 3.3 Core via OpenTK, it features procedural terrain generation, fluid simulation, cave systems, lighting, and both Creative and Survival game modes.

The project consists of two components:
- **VStudioCraft.vsix** — Visual Studio extension hosting the game in a tool window
- **VStudioCraft.Standalone** — Standalone WPF application for testing and iteration without reinstalling the VSIX

## 🎮 Quick Start

**For Players:**
1. Download the latest `.vsix` from [Releases](https://github.com/MercyPillow/VStudioCraft/releases)
2. Double-click to install in Visual Studio 2022+
3. Open Visual Studio → **View** → **Other Windows** → **VStudioCraft**
4. Start mining!

**For Developers:**
```powershell
git clone https://github.com/MercyPillow/VStudioCraft.git
cd VStudioCraft
# Open VStudioCraft.sln in Visual Studio and press F5
```

## ✨ Features

### 🌍 World & Terrain
- **Procedural generation** — 4-octave Perlin heightmap with layered grass/dirt/stone columns
- **Biomes** — Beach shorelines, oak tree forests, and underground cave systems
- **Ore veins** — Coal, Iron, Gold, Redstone, and Diamond with authentic Alpha Y-level distribution
- **Fluid simulation** — Source-driven water and lava flow with 7-cell (water) / 3-cell (lava) horizontal reach, falling mechanics, and dynamic drain on source removal
- **Cave systems** — Worm-style tunneling with parabolic radius tapering and seamless cross-chunk carving
- **Flora** — Procedurally scattered dandelions, roses, tall grass, and mushrooms on grass surfaces

### 💡 Lighting & Rendering
- **Day/night cycle** — 7-minute sun rotation with dusk/dawn color transitions
- **Dual lighting system** — 4-bit sky light + 4-bit block light (lava emits 15, torches emit 14)
- **Celestial bodies** — Procedural sun, moon with craters, and 500-star starfield
- **Cloud layer** — Wind-driven 2D plane at y=108 with fog tinting
- **Distance fog** — 48-block falloff band matching sky color for seamless chunk transitions
- **Greedy meshing** — 5–10× vertex reduction with face culling and two-pass alpha blending
- **Per-vertex baking** — Light values baked into vertex stream with per-axis face shading

### 🎯 Gameplay
- **Two game modes:**
  - **Creative** — Instant block break, unlimited placement, no damage
  - **Survival** — 20 HP (10 hearts), fall damage, drowning, void damage, respawn system
- **37 block types** — Including flowing water/lava, ores, trees, flowers, torches, and more
- **Swimming mechanics** — Reduced gravity, capped terminal velocity, upward thrust, camera bob
- **Air supply** — 20-bubble oxygen meter with 15-second depletion and drowning damage
- **Hotbar HUD** — 9-slot inventory with procedurally rendered icons and block names
- **Health & hunger UI** — Heart sprites, drumstick hunger bar, and bubble air meter

### ⚙️ Technical
- **Off-thread generation** — Multi-worker job system for terrain generation and chunk meshing
- **Chunk streaming** — 16×128×16 chunks with view radius 6 and unload hysteresis at radius 9
- **Deterministic seeding** — Per-chunk and per-column hashed RNG for reproducible features
- **Persistence** — Custom gzipped binary world format (`.vsc1`) with modified-chunk tracking
- **Dedicated render thread** — OpenGL context ownership with cross-thread input state synchronization

## 🚀 Getting Started

### Prerequisites
- **Visual Studio 2022** or later (Community, Professional, or Enterprise)
- **.NET Framework 4.7.2** SDK
- **OpenGL 3.3**-capable graphics hardware

### Building the Extension

1. Clone the repository:
   ```bash
   git clone https://github.com/MercyPillow/VStudioCraft.git
   cd VStudioCraft
   ```

2. Open `VStudioCraft.sln` in Visual Studio

3. Build the solution (Ctrl+Shift+B)

4. Press F5 to launch the Experimental Instance with the extension loaded

5. In the Experimental Instance, open **View → Other Windows → VStudioCraft** to launch the game window

### Running the Standalone Version

1. Set `VStudioCraft.Standalone` as the startup project

2. Press F5 to run

The standalone version is ideal for rapid iteration without reinstalling the VSIX after each change.

## 🎮 Controls

| Key | Action |
|-----|--------|
| **WASD** | Move |
| **Space** | Jump / Swim up |
| **Ctrl** | Sprint |
| **Mouse** | Look around |
| **Left Click** | Break block |
| **Right Click** | Place block |
| **1-9** | Select hotbar slot |
| **F3** | Toggle Creative ↔ Survival |
| **Esc** | Release mouse cursor |

## 🧱 Block Types

**37 blocks currently implemented:**

Air, Grass, Dirt, Stone, Sand, Cobblestone, Bedrock, Gravel, Clay, Coal/Iron/Gold/Diamond/Redstone Ore, Wood Log, Planks, Leaves, Water (source + flowing), Lava (source + flowing), Gold/Iron/Diamond Block, Bricks, TNT, Bookshelf, Mossy Cobblestone, Obsidian, Sponge, Glass, Wool, Torch, Dandelion, Rose, Brown/Red Mushroom, Tall Grass

## 💾 World Format

Worlds are saved to `%LOCALAPPDATA%\VStudioCraft\Worlds\` in a custom gzipped binary format:
- **Magic header:** `VSC1`
- **Version:** 3
- **Contents:** Modified chunks, player position/rotation, game mode, HP
- **Compression:** gzip with per-chunk modified flag to minimize save size

## 🏗️ Architecture

```
VStudioCraft/
├── src/
│   ├── VStudioCraft/              # VS extension project
│   │   ├── Game/                  # Core game engine
│   │   │   ├── Blocks.cs          # Block types, properties, textures
│   │   │   ├── Chunk.cs           # 16×128×16 chunk data structure
│   │   │   ├── ChunkJobSystem.cs  # Off-thread worker pool
│   │   │   ├── ChunkRenderer.cs   # Greedy mesher + GL upload
│   │   │   ├── FluidSimulator.cs  # Water/lava propagation
│   │   │   ├── Game.cs            # Main game loop & state
│   │   │   ├── HudRenderer.cs     # Hearts, hunger, air HUD
│   │   │   ├── Lighting.cs        # Sky + block light propagation
│   │   │   ├── Noise.cs           # Multi-octave Perlin
│   │   │   ├── Player.cs          # Movement, collision, damage
│   │   │   ├── SkyRenderer.cs     # Sun, moon, stars, clouds
│   │   │   ├── TerrainGenerator.cs# Heightmap, ores, trees, caves
│   │   │   ├── WorldSerializer.cs # Save/load system
│   │   │   └── ...
│   │   └── UI/
│   │       └── GameHostControl.xaml  # WPF-wrapped GLControl
│   │
│   └── VStudioCraft.Standalone/   # Standalone WPF harness
│       ├── MainWindow.xaml        # Host window
│       └── App.xaml               # WPF application entry
│
└── features.md                    # Detailed feature audit vs. Alpha 1.1.2_01
```

## 🗺️ Roadmap

See [features.md](features.md) for a comprehensive audit against Minecraft Alpha 1.1.2_01.

### 🎯 Immediate Next Steps
1. **Inventory system** — Item stacks, drops, pick-block
2. **Block hardness & mining time** — Tool-based break speeds and drops
3. **Mobs** — Entity system with passive (pig, cow) and hostile (zombie, creeper) AI
4. **Crafting & furnaces** — Recipe system and smelting
5. **Audio** — Background music, ambient sounds, block-specific SFX

## 🤝 Contributing

Contributions are welcome! Please feel free to submit pull requests or open issues for bugs, feature requests, or suggestions.

### Development Guidelines

- Follow the existing code style and conventions
- Ensure all changes compile without warnings
- Test both the VSIX and Standalone projects
- Document any new features or changes in behavior

### Setting Up for Development

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/amazing-feature`
3. Commit your changes: `git commit -m 'Add amazing feature'`
4. Push to the branch: `git push origin feature/amazing-feature`
5. Open a Pull Request

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

**Disclaimer:** This is an educational recreation and is not affiliated with Mojang Studios or Microsoft Corporation. Minecraft is a trademark of Mojang Synergies AB.

## 🙏 Acknowledgments

- Inspired by **Minecraft Alpha 1.1.2_01** (2010-09-18)
- Built with [**OpenTK**](https://opentk.net/) for OpenGL bindings
- Perlin noise based on classic multi-octave implementation
- Greedy meshing algorithm adapted from voxel community techniques

## 📸 Screenshots

*Coming soon! Screenshots and gameplay videos will be added in a future update.*

---

<div align="center">

**Made with ❤️ for Visual Studio developers who want to mine diamonds while debugging code.**

[Report Bug](https://github.com/MercyPillow/VStudioCraft/issues) · [Request Feature](https://github.com/MercyPillow/VStudioCraft/issues) · [View Releases](https://github.com/MercyPillow/VStudioCraft/releases)

</div>
