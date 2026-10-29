using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using System.Collections.Generic;
using TMPro;
using UnityEngine.SceneManagement;

public class NoteManager : MonoBehaviour
{
    public static NoteManager Instance;

    [Header("UI References")]
    [SerializeField] private GameObject noteDisplayPrefab;
    [SerializeField] private TMP_Text journalText, noteText, journalPageNum, notePageNum;
    [SerializeField] private RectTransform journalContainer, notesContainer;
    public CanvasGroup uiCG;
    [SerializeField] private CanvasGroup journalCG, noteCG, journalTextCG, noteTextCG, closePageCG;
    public ScrollRect journalSR, notesSR;
    private Camera uiCamera;

    [Header("Journal")]
    public bool readingNote = false;
    public List<NoteDataContent> journalEntries;

    [Header("Sprites")]
    [SerializeField] private Sprite journalEntrySprite, journalMemorySprite, storyNoteSprite, puzzleNoteSprite;

    private List<NoteContent> currentNote;
    private NoteDataContent noteData;
    private string currentNoteAssetName;
    private int currentIndex = 0;

    private InventoryCursor inventoryCursor;
    private PlayerInput playerInput;
    private InputAction changePage;
    private InputAction closePage;
    private InputAction selectEntry;

    private bool toggledActionMap = false;

    #region Initialization, Singletons & Lifecycle
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        uiCamera = GetComponentInParent<Canvas>().worldCamera;
        OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);

        changePage.started += HandlePageTurn;
        closePage.started += HandleClosePage;
        selectEntry.started += HandleSelectEntry;

        SetAllUIInactive();
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (changePage == null) return;
        changePage.started -= HandlePageTurn;
        closePage.started -= HandleClosePage;
        selectEntry.started -= HandleSelectEntry;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        inventoryCursor = UIManager.Instance.inventoryCursor;
        playerInput = PlayerController.Instance.playerInput;
        changePage = playerInput.actions.FindActionMap("Inventory").FindAction("ChangePage");
        closePage = playerInput.actions.FindActionMap("Inventory").FindAction("ClosePage");
        selectEntry = playerInput.actions.FindActionMap("Inventory").FindAction("Select");
    }

    private void OnDestroy()
    {
        if (PlayerController.Instance != null && readingNote)
            PlayerController.Instance.SwitchToGameplay();
        currentNote = null;
        noteData = null;
        readingNote = false;
        toggledActionMap = false;
    }
    #endregion

    #region Save & State Restoration
    public void RestoreNotesFromSave(List<SerializeableBoolKVP> collectedNotes)
    {
        journalEntries.Clear();

        List<GameObject> notesToDestory = new List<GameObject>();
        foreach (RectTransform note in notesContainer.transform) notesToDestory.Add(note.gameObject);
        foreach (RectTransform journal in journalContainer.transform) notesToDestory.Add(journal.gameObject);
        foreach (GameObject obj in notesToDestory) Destroy(obj);

        foreach (SerializeableBoolKVP note in collectedNotes)
        {
            NoteDataSO noteData = SaveManager.Instance.GetNoteFromDatabase(note.key);

            if (noteData != null) AddNoteToJournal(noteData.data, note.value, note.key);
            else Debug.LogError($"NoteManager: Could not find note '{note.key}' in Note Library");
        }
    }
    #endregion

    #region Journal Organization & Sorting Algorithm
    // Sorts journal entries numerically based on trailing digits in their titles 
    // to ensure pages stay in order regardless of the sequence they are picked up in.
    private void AddNoteToJournal(NoteDataContent data, bool wasRead = false, string noteAsset = "")
    {
        if (journalEntries.Exists(entry => entry.title == data.title)) return;

        journalEntries.Add(data);

        RectTransform parent = data.isNote ? notesContainer : journalContainer;
        GameObject entry = Instantiate(noteDisplayPrefab, parent);
        entry.GetComponent<NoteEntryDisplay>().Setup(data, wasRead, noteAsset);

        // Only sort actual Journal Entries � Memories and anything else stay in pickup order.
        if (!data.isNote && IsJournalEntry(data.title))
        {
            int newNumber = ExtractEntryNumber(data.title);
            int targetIndex = journalContainer.childCount - 1; // default: stay at the end

            for (int i = 0; i < journalContainer.childCount - 1; i++)
            {
                var sibling = journalContainer.GetChild(i).GetComponent<NoteEntryDisplay>();
                if (sibling == null) continue;

                // Skip non-journal-entry siblings (Memories, etc.) so they don't
                // affect where this entry slots in.
                if (!IsJournalEntry(sibling.linkedItem.title)) continue;

                if (ExtractEntryNumber(sibling.linkedItem.title) > newNumber)
                {
                    targetIndex = i;
                    break;
                }
            }

            entry.transform.SetSiblingIndex(targetIndex);
        }

        if (!data.isNote) UIManager.Instance.itemPickupUI?.ShowJournalUpdate();
    }

    private bool IsJournalEntry(string title)
    {
        return !string.IsNullOrEmpty(title) && title.StartsWith("Journal Entry");
    }

    // Parses strings from the end to isolate trailing numbers (e.g., "Journal Entry 3" -> 3) 
    // for sorting purposes.
    private int ExtractEntryNumber(string title)
    {
        if (string.IsNullOrEmpty(title)) return int.MaxValue;

        int i = title.Length - 1;
        while (i >= 0 && char.IsDigit(title[i])) i--;

        string numberPart = title.Substring(i + 1);
        return int.TryParse(numberPart, out int n) ? n : int.MaxValue;
    }
    #endregion

    #region Note Reading & Page Navigation
    public void ReadNote(InteractionEntry currentInteraction)
    {
        NoteDataSO data = currentInteraction.note;

        if (data == null || data.data.note.Count == 0)
        {
            Debug.LogWarning("No note data found.");
            return;
        }

        noteData = data.Clone();
        currentNoteAssetName = data.name;

        if (noteData != null && !noteData.isNote)
        {
            AddNoteToJournal(noteData, false, data.name);
            SaveManager.Instance.RegisterNote(data.name, false);
        }
        else SaveManager.Instance.RegisterNote(data.name, true);

        if (!data.data.isNote) return; 

        currentNote = noteData.note;
        currentIndex = 0;
        readingNote = true;

        if (playerInput.currentActionMap.name != "Inventory")
        {
            PlayerController.Instance.SwitchToInventory();
            toggledActionMap = true;
        }

        SetUIActive(uiCG, true);
        SetUIActive(noteCG, true);
        SetUIActive(noteTextCG, true);
        SetUIActive(closePageCG, true);
        DisplayPage(true);
    }

    public void ReadNote(NoteDataSO note) // Same logic as above, just passing note data straight through
    {
        noteData = note.Clone();
        if (!note.data.isNote) return;

        currentNote = noteData.note;
        currentIndex = 0;
        readingNote = true;

        SetUIActive(uiCG, true);
        SetUIActive(noteCG, true);
        SetUIActive(noteTextCG, true);
        SetUIActive(closePageCG, true);
        DisplayPage(true);
    }

    public void HandlePageTurn(InputAction.CallbackContext obj)
    {
        if (!readingNote) return;

        float val = obj.ReadValue<float>();
        int direction = (int)val;
        int pageIndex = Mathf.Clamp(currentIndex + direction, 0, currentNote.Count - 1);
        if (pageIndex == currentIndex) return; // No more pages to go to
        currentIndex = pageIndex;

        DisplayPage(noteData.isNote);
    }

    private void HandleClosePage(InputAction.CallbackContext obj)
    {
        if (!readingNote) return;
        CloseNote(true);
    }

    private void DisplayPage(bool isNote)
    {
        TMP_Text textObject = isNote ? noteText : journalText;
        NoteContent note = currentNote[currentIndex];
        textObject.text = note.text;

        TMP_Text pageNumText = noteData.isNote ? notePageNum : journalPageNum;
        pageNumText.text = (currentIndex + 1).ToString() + "/" + currentNote.Count.ToString();
    }

    public void CloseNote(bool isDefaultNote)
    {
        if (inventoryCursor.isJournal)
        {
            SetAllUIInactive();
            SetUIActive(uiCG, true);
            SetUIActive(journalCG, true);
            notesContainer.gameObject.SetActive(true);
        }
        else if (toggledActionMap)
        {
            SetAllUIInactive();
            PlayerController.Instance.SwitchToGameplay();
            if (noteData != null && noteData.isNote)
                AddNoteToJournal(noteData, true, currentNoteAssetName);
        }
        else if (!isDefaultNote)
        {
            SetAllUIInactive();
            if (noteData != null && noteData.isNote)
                AddNoteToJournal(noteData, true, currentNoteAssetName);
        }

        currentNote = null;
        noteData = null;
        currentNoteAssetName = null;
        readingNote = false;
        toggledActionMap = false;
    }
    #endregion

    #region UI Hover & Selection Input
    private void HandleSelectEntry(InputAction.CallbackContext obj)
    {
        if (!inventoryCursor.isJournal) return;

        NoteEntryDisplay hovered = GetHoveredEntry();
        if (hovered == null) return;
        SelectEntry(hovered);
    }

    private NoteEntryDisplay GetHoveredEntry()
    {
        RectTransform[] containers = { journalContainer, notesContainer};

        foreach (RectTransform container in containers)
        {
            foreach (RectTransform child in container)
            {
                NoteEntryDisplay display = child.GetComponent<NoteEntryDisplay>();
                if (display == null) continue;

                if (RectTransformUtility.RectangleContainsScreenPoint(
                    child,
                    inventoryCursor.screenPos,
                    uiCamera))
                {
                    return display;
                }
            }
        }

        return null;
    }

    private void SelectEntry(NoteEntryDisplay entry)
    {
        readingNote = true;
        noteData = entry.linkedItem;
        currentNoteAssetName = entry.noteSOName;
        currentNote = noteData.note;
        currentIndex = 0;
        SaveManager.Instance.RegisterNote(entry.noteSOName, true);
        entry.unreadDisplay.enabled = false;

        if (entry.linkedItem.isNote)
        {
            SetUIActive(journalTextCG, false);
            SetUIActive(noteCG, true);
            SetUIActive(noteTextCG, true);
            SetUIActive(closePageCG, true);
            DisplayPage(true);
        }
        else
        {
            SetUIActive(journalTextCG, true);
            SetUIActive(noteTextCG, false);
            SetUIActive(closePageCG, true);
            DisplayPage(false);
            notesContainer.gameObject.SetActive(false);
        }
    }

    private void SetAllUIInactive()
    {
        SetUIActive(uiCG, false);
        SetUIActive(journalCG, false);
        SetUIActive(noteCG, false);
        SetUIActive(journalTextCG, false);
        SetUIActive(noteTextCG, false);
        SetUIActive(closePageCG, false);
    }

    private void SetUIActive(CanvasGroup cg, bool active)
    {
        cg.alpha = active ? 1 : 0;
        cg.interactable = active;
        cg.blocksRaycasts = active;
    }

    public void SetJournalUI(bool active)
    {
        journalCG.alpha = active ? 1 : 0;
        journalCG.interactable = active;
        journalCG.blocksRaycasts = active;
    }
    #endregion
}