# Light Of My Life
Light of My Life is a narrative adventure game focussing on interactive exploration, inventory management, and branching dialogue systems.

Playable Build: [Play on Itch.io](https://lightofmylife.itch.io/light-of-my-life)
My Portfolio of other projects [Portfolio](https://sasharobbins.github.io)

<h1>Systems Overview</h1>

<h2>Save Manager</h2>
- Manages cross scene data persistence, manual and autosave slots, and player profile data (inventories, world states, dialogue choices, quest flags).
- Runs as a singleton and uses JSON serialization with Player-Prefs to store player data across play sessions.
- Uses asynchronous coroutines with validation guards in between scene loading.
- Stores 3 individual save slots, with an autosave slot that updates with the current manual save slot.

<h2>Grid Inventory</h2>
- Resident Evil inspired, grid-based inventory and key-item management, item movement, rotation, and stacking.
- Uses a 2D array matrix for spatial collision and boundary validation.
- Integrates with Scriptable Objects for items, weapons, and recipe databases
- Handles save-data instantiation, that either loads from save data, or reverts to a previous save state if the player respawns.

<h2>Interaction Controller</h2>
- Acts as a centralized controller for all world interactions (pickup, doors, dialogues, notes, maps, moveable objects).
- Uses data driven design via a list of Interaction Entry structures, that are evaluated based on item requirements, bool/int dialogue variables, and event triggers.
- Handles multi stage interactions that are sequenced from the outcome of previous interactions (A door that becomes unlocked when you obtain a key, and dialogue triggers based on the locked state).

<h2>Dialogue Manager</h2>
- Processes branching conversations, typewriter text effects, choice evaluation, and PlayableDirector animations for cutscenes, using Cinemachine cameras for speaking character, or a point of interest.
- Driven by Scriptable Object conversation nodes, that support conditional branching dialogue based on global game variables from previous conversation choices.
- Multiple use cases in dialogue between characters, scene transitions, world event states such as locked doors / new areas of exploration.

<h2>Journal & Note Manager</h2>
- Manages collection, reading, and organizing narrative notes, stories, and journal entries.
- Organizes entries with titles that have numerical prefixes to keep similar notes ordered regardless of pickup sequence.
- Two systems merged into one overall manager, a journal system that gets updated with world interactions and exploration that gives more backstory to the game. Notes are physical world pickups that mostly give directional hints for game progression.

**Disclaimer:** These scripts have been extracted directly from a full-scale Unity project. They are provided here as architectural and code-quality samples for review purposes and are not intended to compile or run standalone outside of their broader project dependencies (such as custom UI prefabs, ScriptableObjects, and Input Action assets).

Copyright (c) 2026 Sasha Robbins. All rights reserved. This repository is published for portfolio and viewing purposes only. Please don’t copy, modify, or redistribute the code without written permission.
