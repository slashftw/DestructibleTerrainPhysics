# DestructibleTerrainPhysics

A 2D destructible terrain system with physics built in Unity engine, highly customisable script parameters, works with any sprite or texture.

![Per-pixel destructible terrain demo](docs/demo-4k.gif)

Above GIF is a little showcase including a ~4K texture / map, the FPS doesn’t drop even on heavy digging scenarios, however a few spikes do occur occasionally, also note that this was the editor view and the build performance may be much better. You can also check the Mobile play (stress) test on the [Clips Folder](docs/) folder.

## Setup

![Basic Setup demo](docs/DTP_TutorialClip0.gif)

**In an existing Project** :

- Import the **.unitypackage** file in your project.
- Select the core script i.e., DestructibleTerrain.cs, then hit Import.

*Use the **EraseCircle(worldPosition, eraseRadius)** API for the dig operations.*

*Erases are **queued and batched** once per frame (BFS detection and collider/visual rebuilds run once per frame regardless of how many calls you make), so you can call it freely.*

*For pointer-driven digging, `InputManager` gives you the world-space position and the terrain under the cursor / touch, so you call the API directly **without** the `Digger` gameobject.*

**In a New Project** :

- Do the same and Choose the optional items if you want to run a test.
- Right Click on the hierarchy > 2D Object > Destructible Terrain.

*This will create a demo 4k texture and a Digger gameobject which bridges the Inputs to the EraseCircle(worldPosition, eraseRadius) API from the DestructibleTerrain.cs script.*

**Notes** :

- The core script is called "DestructibleTerrain.cs" located in ![Core Script](Assets/DTP/DestructibleTerrain.cs).
- Change the terrain by simply drag n' dropping the desired sprite / image onto the SpriteRenderer component of the Destructible Terrain gameobject.
- If the whole Texture falls on interact then make sure to adjust the variables in the inspector, especially the Max Physics Pixels, the default values are set for a 4096x4096 @ 64MB texture, so make sure to play around with them.
- Pointer digging should work in both the Game view and the Simulator view, if it doesn't then try changing the settings on **Window > Analysis > Input Debugger then Options > Simulate Touch input from Mouse or Pen**.
- You can assign your own Particle System asset for the debri particles.
- If you don't want the whole island to fall while having `MaxPhysicsPixels` set to a high number, adjust the `Ground Anchor Y`'s value or simply enable the Anchor checks in the inspector.
- Most of the settings work but some of them may be semi-functional, i will fix them, later ...

## Known Issues

- Actively falling chunks cause the other pieces of chunks falling onto them to *phase* through, when a carved-off chunk’s linear velocity is near zero, the falling pieces *noclip* through them upto a certain depth.

## Requirements

- Unity 2021 LTS + | Developed with Unity 6000.4.6f1
- **URP (2D)**.
- Packages (installed by default in the URP 2D template): `com.unity.inputsystem`, `com.unity.burst`, `com.unity.collections`, `com.unity.mathematics`.
- **Active Input Handling** set to the new Input System Package (or Both).



Leave a ⭐ if you found it helpful !
Feel free to use the script in your games. Attribution welcomed !

## Support me

If this project has been useful to you, consider supporting its development.
https://ko-fi.com/harshkarma

## License

MIT [LICENSE](LICENSE).