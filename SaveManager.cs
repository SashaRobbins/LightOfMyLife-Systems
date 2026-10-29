using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine.SceneManagement;
using System;

public class SaveManager : MonoBehaviour
{
    public static SaveManager Instance;

    private const string MostRecentSlotKey = "MostRecentSaveSlot";
    private const string ActiveManualSlotKey = "ActiveManualSlot";
    private const string TimestampFormat = "MM/dd/yy HH:mm";

    [Header("Game Data")]
    [SerializeField] private ProfileData currentProfileData;
    [SerializeField] private SaveData currentSaveData;

    private bool isGameplayScene = false;
    private bool pendingLoad = false;
    private bool isProcessingLoad = false;
    private int activeProfileSlot;
    private int manualSaveSlot = 1;

    private GameObject player;
    private PlayerEntityBehaviour playerStats;
    private NoteDatabase noteDatabase;
    private ObjectivesSO objectives;
    private DeathScreen deathScreen;
    private float fadeOutTime = 1f;
    private const float CaptureTimeout = 5f;
    private bool lastCaptureSucceeded = false;

    #region Initialization & Singletons

    // Ensures the SaveManager persists across scene loads and auto-creates 
    // if the game is launched directly from a gameplay scene instead of the main menu.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance == null)
        {
            GameObject obj = new GameObject("SaveManager");
            obj.AddComponent<SaveManager>();
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        currentSaveData = new SaveData();
        currentProfileData = new ProfileData();
        manualSaveSlot = PlayerPrefs.GetInt(ActiveManualSlotKey, 1);
        noteDatabase = Resources.Load<NoteDatabase>("NoteLibrary");
        objectives = Resources.Load<ObjectivesSO>("Objectives");
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnApplicationQuit()
    {
        if (pendingLoad) return;
        if (player == null) return;

        StartCoroutine(SaveAutosave());
    }
    #endregion

    #region Save & Load Public API

    public IEnumerator InitNewGame(int slot)
    {
        currentSaveData = new SaveData();
        currentProfileData = new ProfileData();
        CleanupSaveData();

        string profilePath = GetProfilePath(slot);
        if (File.Exists(profilePath)) File.Delete(profilePath);
        LoadProfile(slot);

        manualSaveSlot = slot;
        PlayerPrefs.SetInt(ActiveManualSlotKey, manualSaveSlot);
        PlayerPrefs.Save();

        currentSaveData.sceneName = "Tutorial";
        currentSaveData.health = 5f;
        currentSaveData.sunburnAmount = 0f;
        currentProfileData.dialogueVarInts.Add(new SerializeableIntKVP { key = "obj", value = 0 });
        currentSaveData.lastSavedTimestamp = System.DateTime.Now.ToString
            (TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);

        yield return StartCoroutine(SaveManual(slot));
        yield return StartCoroutine(LoadSceneAsync());
    }

    private void LoadProfile(int slot)
    {
        activeProfileSlot = slot;
        string path = GetProfilePath(slot);
        if (File.Exists(path))
            currentProfileData = JsonUtility.FromJson<ProfileData>(File.ReadAllText(path));
        else
            currentProfileData = new ProfileData();
    }

    private void SaveProfile()
    {
        string contents = JsonUtility.ToJson(currentProfileData);
        File.WriteAllText(GetProfilePath(activeProfileSlot), contents);
    }

    public IEnumerator LoadSave(int slot)
    {
        if (isProcessingLoad) yield break;
        isProcessingLoad = true;

        string path = GetSavePath(slot);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"LoadGame: No file found in Slot {slot}");
            isProcessingLoad = false;
            yield break;
        }

        string saveData = File.ReadAllText(path);
        if (string.IsNullOrEmpty(saveData))
        {
            Debug.LogError("Save file is empty!");
            isProcessingLoad = false;
            yield break;
        }

        CleanupSaveData(); // Clear lists to prevent duplicate data being written
        int profileSlot = slot == 0 ? manualSaveSlot : slot;
        LoadProfile(profileSlot);

        JsonUtility.FromJsonOverwrite(saveData, currentSaveData);
        if (slot != 0)
        {
            manualSaveSlot = slot;
            PlayerPrefs.SetInt(ActiveManualSlotKey, manualSaveSlot);
            PlayerPrefs.Save();
        }

        pendingLoad = true;
        isProcessingLoad = false;
        yield return StartCoroutine(LoadSceneAsync());
    }

    public void DeleteSave(int slot)
    {
        string path = GetSavePath(slot);
        if (File.Exists(path)) File.Delete(path);

        string profilePath = GetProfilePath(slot);
        if (File.Exists(profilePath)) File.Delete(profilePath);

        if (PlayerPrefs.GetInt(MostRecentSlotKey, -1) == slot)
        {
            if (IsSlotOccupied(0)) PlayerPrefs.SetInt(MostRecentSlotKey, 0);
            else PlayerPrefs.DeleteKey(MostRecentSlotKey);
        }
        PlayerPrefs.Save();
    }

    public void ReloadCurrentSave()
    {
        int recentSlot = GetMostRecentSaveSlot();
        StartCoroutine(LoadSave(recentSlot));
    }

    public IEnumerator SaveAutosave()
    {
        yield return StartCoroutine(CaptureGameState(currentSaveData.savePointPos));
        if (!lastCaptureSucceeded)
        {
            Debug.LogWarning("Autosave skipped - capture failed");
            yield break;
        }

        UIManager.Instance.autosaveIcon.ShowAutosaveIcon();
        currentSaveData.lastSavedTimestamp = System.DateTime.Now.ToString
            (TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
        string contents = JsonUtility.ToJson(currentSaveData);
        File.WriteAllText(GetSavePath(0), contents);

        PlayerPrefs.SetInt(MostRecentSlotKey, 0);
        PlayerPrefs.Save();
        Debug.Log("Autosave Completed (Slot 0)");

        yield return null;
    }

    public IEnumerator SaveManual(int slot, bool captureState = true)
    {
        if (slot == 0) yield break;

        Vector3 savePos = currentSaveData.savePointPos;
        if (PlayerController.Instance != null)
            savePos = PlayerController.Instance.gameObject.transform.position;

        yield return StartCoroutine(CaptureGameState(savePos));
        if (!lastCaptureSucceeded)
        {
            Debug.LogWarning("Autosave skipped - capture failed");
            yield break;
        }

        manualSaveSlot = slot;
        PlayerPrefs.SetInt(ActiveManualSlotKey, manualSaveSlot);
        PlayerPrefs.Save();

        currentSaveData.lastSavedTimestamp = System.DateTime.Now.ToString
            (TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);

        string contents = JsonUtility.ToJson(currentSaveData);
        File.WriteAllText(GetSavePath(slot), contents);

        PlayerPrefs.SetInt(MostRecentSlotKey, slot);
        PlayerPrefs.Save();
        Debug.Log($"Manual Save Completed (Slot {slot})");
        yield return null;
    }

    public Coroutine HandleGameSave(bool hasExit, Action onComplete = null)
    {
        return StartCoroutine(HandleSaveRoutine(hasExit, onComplete));
    }

    private IEnumerator HandleSaveRoutine(bool exitOnSave, Action onComplete)
    {
        yield return StartCoroutine(SaveAutosave());

        if (!exitOnSave)
        {
            onComplete?.Invoke();
            yield break;
        }

        // Clear Manager Instances
        if (DialogueManager.Instance != null)
            Destroy(DialogueManager.Instance.gameObject);
        if (InteractPrompt.Instance != null)
            Destroy(InteractPrompt.Instance.gameObject);
        if (MapManager.Instance != null)
            Destroy(MapManager.Instance.gameObject);
        if (NoteManager.Instance != null)
            Destroy(NoteManager.Instance.gameObject);
        if (AudioAlertSystem.Instance != null)
            Destroy(AudioAlertSystem.Instance.gameObject);
        if (UIManager.Instance != null)
            Destroy(UIManager.Instance.gameObject);
        if (PlayerController.Instance != null)
            Destroy(PlayerController.Instance.gameObject);

        Time.timeScale = 1f;
        isGameplayScene = false;

        currentSaveData = new SaveData();
        currentProfileData = new ProfileData();
        DialogueVariableManager.Instance.ClearLists();

        SceneManager.LoadSceneAsync(0);
        yield return null;
    }

    #region File Path & Utility Helpers
    private string GetProfilePath(int slot)
    {
        return Path.Combine(Application.persistentDataPath, $"profile_{slot}.json");
    }

    public string GetSavePath(int slot)
    {
        string fileName = (slot == 0) ? "autosave.json" : $"save_manual_{slot}.json";
        return Path.Combine(Application.persistentDataPath, fileName);
    }

    public ProfileData ReadProfileFile(int slot)
    {
        string path = GetProfilePath(slot);
        if (!File.Exists(path)) return null;
        string contents = File.ReadAllText(path);
        if (string.IsNullOrEmpty(contents)) return null;
        return JsonUtility.FromJson<ProfileData>(contents);
    }

    public SaveData ReadSaveFile(int slot)
    {
        string path = GetSavePath(slot);
        if (!File.Exists(path)) return null;
        string contents = File.ReadAllText(path);
        if (string.IsNullOrEmpty(contents)) return null;
        return JsonUtility.FromJson<SaveData>(contents);
    }

    public bool IsSlotOccupied(int slot)
    {
        return File.Exists(GetSavePath(slot));
    }

    public int GetManualSaveSlot()
    {
        return manualSaveSlot;
    }

    // Returns true if at least one save file exists across the given slot range.
    public bool HasAnySave(int totalSlots)
    {
        for (int i = 0; i < totalSlots; i++)
        {
            if (IsSlotOccupied(i)) return true;
        }
        return false;
    }

    // Returns the slot index with the most recently saved slot, or -1 if no saves exist.
    public int GetMostRecentSaveSlot()
    {
        if (!PlayerPrefs.HasKey(MostRecentSlotKey)) return -1;

        int targetedSlot = PlayerPrefs.GetInt(MostRecentSlotKey);
        if (IsSlotOccupied(targetedSlot)) return targetedSlot;

        return -1;
    }

    private void CleanupSaveData()
    {
        currentSaveData.inventoryItems.Clear();
        currentSaveData.sisterItems.Clear();
        currentSaveData.pickedUpWorldItemIDs.Clear();
        currentSaveData.objectTransforms.Clear();
        currentSaveData.defeatedEnemyIDs.Clear();
        currentSaveData.savedDoors.Clear();
        currentSaveData.savedDrawers.Clear();
    }
    #endregion
    #endregion

    #region Scene Transition & Coroutine Flows
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!isGameplayScene) return;

        if (PlayerController.Instance == null) 
        {
            StartCoroutine(DelayedSpawn(scene));
            return;
        }

        HandleSceneReady(scene);
    }

    private IEnumerator DelayedSpawn(Scene scene)
    {
        yield return new WaitForEndOfFrame();

        yield return new WaitUntil(() =>
            PlayerController.Instance != null
        );

        HandleSceneReady(scene);
    }

    private void HandleSceneReady(Scene scene)
    {
        player = PlayerController.Instance.gameObject;
        playerStats = player.GetComponent<PlayerEntityBehaviour>();
        deathScreen = UIManager.Instance.deathScreen;

        if (pendingLoad && scene.name == currentSaveData.sceneName)
        {
            StartCoroutine(DelayedLoad());
        }
        else
        {
            currentSaveData.savePointActivated = false;

            currentSaveData.pickedUpWorldItemIDs.Clear();
            currentSaveData.defeatedEnemyIDs.Clear();
            currentSaveData.objectTransforms.Clear();
            currentSaveData.savedDoors.Clear();
            currentSaveData.savedDrawers.Clear();

            SceneSpawnPoint spawn = FindFirstObjectByType<SceneSpawnPoint>();
            if (spawn != null)
            {
                currentSaveData.savePointPos = spawn.GetSceneSpawnPos();
                PlayerController.Instance.controller.enabled = false;
                PlayerController.Instance.gameObject.transform.position = currentSaveData.savePointPos;
                PlayerController.Instance.controller.enabled = true;
            }

            pendingLoad = false;
            StartCoroutine(SaveAutosave());
        }
    }

    // Wait for grid inventory cells to finish building their layouts from save data 
    // before restoring player health, sunburn status, and inventory item snapshots.
    private IEnumerator DelayedLoad()
    {
        yield return new WaitForEndOfFrame();

        yield return new WaitUntil(() =>
            UIManager.Instance != null &&
            UIManager.Instance.inventory != null &&
            UIManager.Instance.inventory.cells != null &&
            UIManager.Instance.inventory.cells.Count > 0
        );

        GridInventory inventory = UIManager.Instance.inventory;
        GridInventory sisterInventory = UIManager.Instance.sisterInventory;

        inventory.LoadFromSaveData(currentSaveData.inventoryItems, currentProfileData.inventoryKeyItems);
        sisterInventory.LoadFromSaveData(currentSaveData.sisterItems, currentProfileData.sisterKeyItems);
        GridInventory.HideyHops = currentProfileData.hideyHops;

        yield return new WaitUntil(() =>
           inventory.loadCompleted &&
           sisterInventory.loadCompleted
       );

        player = PlayerController.Instance.gameObject;
        playerStats = player.GetComponent<PlayerEntityBehaviour>();
        PlayerController.Instance.sunburnController.ForceResetCounts();

        if (!currentSaveData.savePointActivated)
        {
            SceneSpawnPoint spawn = FindFirstObjectByType<SceneSpawnPoint>();
            if (spawn != null) currentSaveData.savePointPos = spawn.GetSceneSpawnPos();
        }

        float loadHealth = currentSaveData.health > 0f ? currentSaveData.health : 5f; // Guard in delayed load to make sure the player doesnt spawn in with no health
        float loadSunburn = currentSaveData.sunburnAmount < 1f ? currentSaveData.sunburnAmount : 0f; // Same guard as health but making sure the player doesnt spawn with max sunburn
        playerStats.RestoreFromSave(loadHealth, loadSunburn);

        DialogueVariableManager.Instance.RestoreFromSnapshot
            (currentProfileData.dialogueVarBools, currentProfileData.dialogueVarInts);

        NoteManager.Instance.RestoreNotesFromSave(currentProfileData.collectedNotes);

        PlayerController.Instance.controller.enabled = false;
        PlayerController.Instance.gameObject.transform.position = currentSaveData.savePointPos;
        PlayerController.Instance.controller.enabled = true;

        StartCoroutine(FadeOut());

        pendingLoad = false;
        yield return null;
    }

    private IEnumerator FadeOut()
    {
        if (deathScreen == null) deathScreen = UIManager.Instance.deathScreen;

        float elapsed = 0f;

        while (elapsed < fadeOutTime)
        {
            elapsed += Time.unscaledDeltaTime;
            deathScreen.fadeUI.alpha = 1 - (elapsed / fadeOutTime);
            yield return null;
        }

        deathScreen.fadeUI.alpha = 0f;
        yield break;
    }

    private IEnumerator LoadSceneAsync()
    {
        isGameplayScene = true;
        AsyncOperation sceneLoad = SceneManager.LoadSceneAsync(currentSaveData.sceneName);

        while (!sceneLoad.isDone) yield return null;
    }
    #endregion

    #region State Capture & Serialization
    private bool IsCaptureReady()
    {
        return PlayerController.Instance != null
            && UIManager.Instance != null
            && UIManager.Instance.inventory != null
            && UIManager.Instance.sisterInventory != null
            && UIManager.Instance.inventory.loadCompleted
            && UIManager.Instance.sisterInventory.loadCompleted;
    }

    // Safeguard: Loops until player stats, inventories, and UI load states are fully initialized 
    // before serializing game data to prevent saving corrupted or empty inventory states.
    public IEnumerator CaptureGameState(Vector3 savePos)
    {
        lastCaptureSucceeded = false;

        float deadline = Time.unscaledTime + CaptureTimeout;
        while (!IsCaptureReady())
        {
            if (Time.unscaledTime >= deadline)
            {
                Debug.LogWarning("CaptureGamestate timed out - player/UI not ready");
                yield break;
            }
            yield return null;
        }

        player = PlayerController.Instance.gameObject;
        PlayerEntityBehaviour playerStats = player.GetComponent<PlayerEntityBehaviour>();
        GridInventory inventory = UIManager.Instance.inventory;
        GridInventory sisterInventory = UIManager.Instance.sisterInventory;

        yield return new WaitUntil(() =>
            inventory.loadCompleted &&
            sisterInventory.loadCompleted
        );

        CollectSaveData(
            playerStats.Health,
            playerStats.sunburnMeterScale.x,
            savePos,
            SceneManager.GetActiveScene().name,
            inventory.GetSaveSnapshot(inventory.items),
            sisterInventory.GetSaveSnapshot(sisterInventory.items)
        );

        lastCaptureSucceeded = true;
        Debug.Log("CaptureGameState successful");
        yield return null;
    }

    public void CollectSaveData(float health, float sunburnAmount, Vector3 savePos,
                                string sceneName, List<SerializeableItemEntry> invItems,
                                List<SerializeableItemEntry> sisInvItems)
    {
        currentSaveData.lastSavedTimestamp = System.DateTime.Now.ToString
            (TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
        currentSaveData.sceneName = sceneName;
        currentSaveData.savePointPos = savePos;
        currentSaveData.health = health;
        currentSaveData.sunburnAmount = sunburnAmount;
        currentSaveData.inventoryItems = invItems;
        currentSaveData.sisterItems = sisInvItems;
    }
    #endregion

    #region World & Entity Getters / Setters
    public void SetSavePointActivated(bool value)
    {
        currentSaveData.savePointActivated = value;
    }

    public SerializableTransform GetWorldTransform(string id)
    {
        return currentSaveData.objectTransforms.Find(t => t.objID == id);
    }

    public void UpdateWorldTransform(bool valid, bool moveLocked, string id, Vector3 pos, Vector3 rot)
    {
        int index = currentSaveData.objectTransforms.FindIndex(t => t.objID == id);
        SerializableTransform newData = new SerializableTransform 
        { isValid = valid, moveLocked = moveLocked, objID = id, pos = pos, rot = rot };

        if (index != -1) currentSaveData.objectTransforms[index] = newData;
        else currentSaveData.objectTransforms.Add(newData);
    }

    public void UpdateKeyItem(GridInventory inv)
    {
        List<SerializeableItemEntry> snapshot = inv.GetSaveSnapshot(inv.keyItems);

        if (inv == UIManager.Instance.inventory) currentProfileData.inventoryKeyItems = snapshot;
        else if (inv == UIManager.Instance.sisterInventory) currentProfileData.sisterKeyItems = snapshot;
        else return;

        currentProfileData.hideyHops = GridInventory.HideyHops;
        SaveProfile();
    }

    public DoorSaveData GetDoorState(string id)
    {
        return currentSaveData.savedDoors.Find(d => d.doorID == id);
    }

    public void UpdateDoorState(bool valid, string id, bool open, bool locked, DoorSide side)
    {
        int index = currentSaveData.savedDoors.FindIndex(d => d.doorID == id);
        DoorSaveData newData = new DoorSaveData 
        { isValid = valid, doorID = id, isOpen = open, isLocked = locked, side = side };
        if (index != -1) currentSaveData.savedDoors[index] = newData;
        else currentSaveData.savedDoors.Add(newData);
    }

    public DrawerSaveData GetDrawerState(string id)
    {
        return currentSaveData.savedDrawers.Find(d => d.drawerID == id);
    }

    public void UpdateDrawerState(bool valid, string id, bool open, bool locked, bool hasSpawned)
    {
        int index = currentSaveData.savedDrawers.FindIndex(d => d.drawerID == id);
        DrawerSaveData newData = new DrawerSaveData
        { isValid = valid, drawerID = id, isOpen = open, isLocked = locked, hasSpawned = hasSpawned};
        if (index != -1) currentSaveData.savedDrawers[index] = newData;
        else currentSaveData.savedDrawers.Add(newData);
    }

    public void RegisterNote(string noteName, bool hasRead)
    {
        int index = currentProfileData.collectedNotes.FindIndex(note => note.key == noteName);
        SerializeableBoolKVP boolData = new SerializeableBoolKVP();
        boolData.key = noteName;
        boolData.value = hasRead;

        if (index != -1) currentProfileData.collectedNotes[index] = boolData;
        else currentProfileData.collectedNotes.Add(boolData);

        SaveProfile();
    }

    public NoteDataSO GetNoteFromDatabase(string id)
    {
        if (noteDatabase == null) return null;
        return noteDatabase.GetNoteByName(id);
    }

    public int GetInteractionState(string id)
    {
        SerializeableIntKVP interactData = currentProfileData.interactionStates.Find(kvp => kvp.key == id);
        if (string.IsNullOrEmpty(interactData.key)) return -1;
        return interactData.value;
    }

    public void UpdateInteractionState(string id, int value)
    {
        int index = currentProfileData.interactionStates.FindIndex(t => t.key == id);
        SerializeableIntKVP intData = new SerializeableIntKVP();
        intData.key = id;
        intData.value = value;

        if (index != -1) currentProfileData.interactionStates[index] = intData;
        else currentProfileData.interactionStates.Add(intData);

        SaveProfile();
    }

    public bool IsEnemyDead(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return currentSaveData.defeatedEnemyIDs.Contains(id);
    }

    public void RegisterEnemyDeath(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!currentSaveData.defeatedEnemyIDs.Contains(id))
            currentSaveData.defeatedEnemyIDs.Add(id);
    }

    public bool IsWorldItemPickedUp(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return currentSaveData.pickedUpWorldItemIDs.Contains(id);
    }

    public void RegisterWorldItemPickup(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!currentSaveData.pickedUpWorldItemIDs.Contains(id))
            currentSaveData.pickedUpWorldItemIDs.Add(id);
    }

    public bool GetSceneBool(string key)
    {
        return currentProfileData.sceneTriggeredBools.Find(kvp => kvp.key == key).value;
    }

    public void SetSceneBool(string key, bool value)
    {
        int index = currentProfileData.sceneTriggeredBools.FindIndex(t => t.key == key);
        SerializeableBoolKVP boolData = new SerializeableBoolKVP();
        boolData.key = key;
        boolData.value = value;

        if (index != -1) currentProfileData.sceneTriggeredBools[index] = boolData;
        else currentProfileData.sceneTriggeredBools.Add(boolData);

        SaveProfile();
    }

    public int GetSceneInt(string key)
    {
        return currentProfileData.sceneTriggeredInts.Find(kvp => kvp.key == key).value;
    }

    public void SetSceneInt(string key, int value)
    {
        int index = currentProfileData.sceneTriggeredInts.FindIndex(t => t.key == key);
        SerializeableIntKVP intData = new SerializeableIntKVP();
        intData.key = key;
        intData.value = value;

        if (index != -1) currentProfileData.sceneTriggeredInts[index] = intData;
        else currentProfileData.sceneTriggeredInts.Add(intData);

        SaveProfile();
    }

    public void CaptureDialogueVariables()
    {
        List<SerializeableBoolKVP> bools = DialogueVariableManager.Instance.GetSnapshotBools();
        List<SerializeableIntKVP> ints = DialogueVariableManager.Instance.GetSnapshotInts();

        foreach (var entry in bools)
        {
            int index = currentProfileData.dialogueVarBools.FindIndex(t => t.key == entry.key);
            if (index != -1) currentProfileData.dialogueVarBools[index] = entry;
            else currentProfileData.dialogueVarBools.Add(entry);
        }

        foreach (var entry in ints)
        {
            int index = currentProfileData.dialogueVarInts.FindIndex(t => t.key == entry.key);
            if (index != -1) currentProfileData.dialogueVarInts[index] = entry;
            else currentProfileData.dialogueVarInts.Add(entry);
        }

        SaveProfile();
    }

    public int GetDialogueInt(string key)
    {
        SerializeableIntKVP match = currentProfileData.dialogueVarInts.Find(kvp => kvp.key == key);
        if (string.IsNullOrEmpty(match.key)) return -1;
        return match.value;
    }

    public void SetDialogueInt(string key, int value)
    {
        int index = currentProfileData.dialogueVarInts.FindIndex(t => t.key == key);
        SerializeableIntKVP intData = new SerializeableIntKVP();
        intData.key = key;
        intData.value = value;

        if (index != -1) currentProfileData.dialogueVarInts[index] = intData;
        else currentProfileData.dialogueVarInts.Add(intData);

        SaveProfile();
    }

    public bool GetDialogueBool(string key)
    {
        SerializeableBoolKVP match = currentProfileData.dialogueVarBools.Find(kvp => kvp.key == key);
        if (string.IsNullOrEmpty(match.key)) return false;
        return match.value;
    }

    public void SetDialogueBool(string key, bool value)
    {
        int index = currentProfileData.dialogueVarBools.FindIndex(t => t.key == key);
        SerializeableBoolKVP boolData = new SerializeableBoolKVP();
        boolData.key = key;
        boolData.value = value;

        if (index != -1) currentProfileData.dialogueVarBools[index] = boolData;
        else currentProfileData.dialogueVarBools.Add(boolData);

        SaveProfile();
    }

    [ContextMenu("Purge All Saves")]
    public void DeleteSaves()
    {
        for (int i = 0; i <= 3; i++) DeleteSave(i);
        Debug.Log("Saves Purged");
    }

    #endregion
}

[System.Serializable]
public class ProfileData
{
    [Header("Notes")]
    public List<SerializeableBoolKVP> collectedNotes = new List<SerializeableBoolKVP>();

    [Header("Key Inventory")]
    public int hideyHops;
    public List<SerializeableItemEntry> inventoryKeyItems = new List<SerializeableItemEntry>();
    public List<SerializeableItemEntry> sisterKeyItems = new List<SerializeableItemEntry>();

    [Header("Dialogue Variable Manager")]
    public List<SerializeableBoolKVP> dialogueVarBools = new List<SerializeableBoolKVP>();
    public List<SerializeableIntKVP> dialogueVarInts = new List<SerializeableIntKVP>();

    [Header("Interaction & Scene State")]
    public List<SerializeableIntKVP> interactionStates = new List<SerializeableIntKVP>();
    public List<SerializeableBoolKVP> sceneTriggeredBools = new List<SerializeableBoolKVP>();
    public List<SerializeableIntKVP> sceneTriggeredInts = new List<SerializeableIntKVP>();
}

[System.Serializable]
public class SaveData
{
    public string lastSavedTimestamp;

    [Header("Scene & Position")] // Current scene and player position
    public string sceneName;
    public Vector3 savePointPos;
    public bool savePointActivated;

    [Header("Player State")] // Player health and sunburn amount
    public float health;
    public float sunburnAmount;

    [Header("Inventory")] // Items in player and sister inventory and key inventory
    public List<SerializeableItemEntry> inventoryItems = new List<SerializeableItemEntry>();
    public List<SerializeableItemEntry> sisterItems = new List<SerializeableItemEntry>();

    [Header("World State")]
    public List<string> pickedUpWorldItemIDs = new List<string>();
    public List<SerializableTransform> objectTransforms = new List<SerializableTransform>();
    public List<string> defeatedEnemyIDs = new List<string>();
    public List<DoorSaveData> savedDoors = new List<DoorSaveData>();
    public List<DrawerSaveData> savedDrawers = new List<DrawerSaveData>();
}

[System.Serializable]
public struct SerializeableItemEntry
{
    public string saveID;
    public int itemCount;
    public bool rotated;
    public Vector2Int gridPos;
}

[System.Serializable]
public struct SerializableTransform
{
    public bool isValid;
    public bool moveLocked;
    public string objID;
    public Vector3 pos;
    public Vector3 rot;
}

[System.Serializable]
public struct DoorSaveData
{
    public bool isValid;
    public string doorID;
    public bool isOpen;
    public bool isLocked;
    public DoorSide side;
}

[System.Serializable]
public struct DrawerSaveData
{
    public bool isValid;
    public string drawerID;
    public bool isOpen;
    public bool isLocked;
    public bool hasSpawned;
}

[System.Serializable]
public struct SerializeableBoolKVP
{
    public string key;
    public bool value;
}

[System.Serializable]
public struct SerializeableIntKVP
{
    public string key;
    public int value;
}