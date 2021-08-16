# Illusion_Deformers

HS2 for now.
Copy the .dll to BepInEx\Plugins and the .zipmod to (my)mods. 

"Squeezer", "Bulger", "Mover", "Rotator" accessories in the "Arms" category.

- "Adjustment 01" is the selector: move around and scale to set the area that should be deformed. There's a sphere/capsule attached to give you an idea which area is being deformed. You can turn it completely transparent with the color option once it's in place.
- "Adjustment 02" is for adjusting the deformation: Position changes the offset, Scale X is the strength, Scale Y is the falloff. The values aren't restricted so you can make glitchy eldritch monsters, but in general you probably want low values here, under 2. With the capsule variants, scale Z controls how much the deformation is based on the center of the capsule (z=1) vs the axis of the capsule (z=0).
- You can add multiple deformers, order is top to bottom. Each deformer uses the result of the previous deformers as the base.
- Switch the accessory's "Filter"-Material shader from "Standard" to something else to only deform renderers with that shader (with Material Editor). Switch to "Clothes True" to only deform clothes and not skin for example. Also helps with performance.
- Deformers can be accessed, adjusted and used for animations in studio with HSPE. Just search for "move" bones.

Quick demo:
https://streamable.com/ed2qkz
