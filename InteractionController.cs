using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UI;
using System.Collections;

public class InteractionController : MonoBehaviour
{
    public static event System.Action<InteractionController> OnControllerCompleted;
    public List<InteractionEntry> interactionData;
    private List<InteractionEntry> completedInteractions = new List<InteractionEntry>();
    private Queue<InteractionEntry> pendingRewards = new Queue<InteractionEntry>();
    private bool processingRewards = false;
    [Tooltip("Position where the prompt is displayed relative to the object.")]
    [SerializeField] private Vector3 promptOffset;
    private BoxCollider doorObject;
    private GridInventory inventory;
    private GridInventory vidaInventory;
    private bool eventTriggered = false;
    private bool waitingDialogueEnd = false;
    private bool waitingMoveEnd = false;
    private InteractionEntry pendingVariableEntry = null;
    [SerializeField] private int interactionIndex = -1;

    public bool triggerDialogueOnEnter = false;
    private bool isQuitting = false;

    private Door door;
    private Drawer drawer;
    private WeaponUI weaponUI;

    #region Initialization & Event Registration
    void Start()
    {
        inventory = UIManager.Instance.inventory;
        vidaInventory = UIManager.Instance.sisterInventory;
        weaponUI = FindFirstObjectByType<WeaponUI>();

        foreach (InteractionEntry entry in interactionData)
        {
            if (entry.interact.interactionType == InteractionType.Door)
            {
                doorObject = gameObject.GetComponentInChildren<BoxCollider>();
                door = GetComponent<Door>();
                DoorSaveData doorData = SaveManager.Instance.GetDoorState(door.doorObjectID);
                if (doorData.isValid && !doorData.isLocked)
                {
                    foreach (InteractionEntry interact in interactionData)
                    {
                        if (interact.condition == ConditionType.RequiresItem)
                            interact.condition = ConditionType.None;
                    }
                }
            }

            if (entry.interact.interactionType == InteractionType.Move)
            {
                MoveObject moveableObject = GetComponent<MoveObject>();
                SerializableTransform moveData = SaveManager.Instance.GetWorldTransform(moveableObject.objectID);
                if (moveData.isValid && !moveData.moveLocked)
                {
                    foreach (InteractionEntry interact in interactionData)
                    {
                        if (interact.condition == ConditionType.RequiresItem)
                            interact.condition = ConditionType.None;
                    }
                }
            }

            else if (entry.interact.interactionType == InteractionType.Drawer)
            {
                // Drawer uses the same doorObject logic for prompt positioning
                doorObject = gameObject.GetComponentInChildren<BoxCollider>();
                drawer = GetComponent<Drawer>();
            }
        }

        int storedInteractIndex = -1;
        WorldInteractable worldInteract = GetComponent<WorldInteractable>();
        if (worldInteract != null)
        {
            storedInteractIndex = SaveManager.Instance.GetInteractionState(worldInteract.worldInteractableID);
            if (storedInteractIndex != -1) // List doesnt exist and avoid false update interact list
            {
                for (int i = 0; i <= storedInteractIndex; i++)
                {
                    completedInteractions.Add(interactionData[i]);
                }
            }
        }
            
        DialogueManager.OnDialogueEnded += DialogueFinished;
        MoveObject.OnMovingEnded += MoveFinished;
    }

    public void TriggerEvent()
    {
        eventTriggered = true;
        Interact();
    }

    private void OnApplicationQuit()
    {
        isQuitting = true;
    }

    private void OnDestroy()
    {
        if (isQuitting) return;
        DialogueManager.OnDialogueEnded -= DialogueFinished;
        MoveObject.OnMovingEnded -= MoveFinished;
    }
    #endregion

    #region Core Interaction & Condition Evaluation
    public void Interact()
    {
        InteractionEntry entry = GetActiveInteraction();
        if (entry != null)
        {
            bool deferVariables = entry.useVariableLogic &&
                                  entry.interact.interactionType == InteractionType.Dialogue;

            if (entry.useVariableLogic && entry.triggeredVariables != null && !deferVariables)
            {
                foreach (var effect in entry.triggeredVariables)
                {
                    effect.Apply();
                }
            }

            switch (entry.interact.interactionType)
            {
                case (InteractionType.Pickup): PickUp(entry); break;
                case (InteractionType.Dialogue): Dialogue(entry); break;
                case (InteractionType.Door): UseDoor(entry); break;
                case (InteractionType.Note): Note(entry); CheckForPendingRewards(); break;
                case (InteractionType.Move): Move(entry); break;
                case (InteractionType.Confirm): CheckConfirm(entry); break;
                case (InteractionType.Map): MapInteract(entry); break;
                case (InteractionType.Drawer): UseDrawer(entry); break;
                case (InteractionType.Polaroid): PolaroidSave(entry); break;
            }
        }
    }

    // Evaluates the list of interaction entries against items, event flags, 
    // and dialogue variables to find the highest-priority valid interaction state.
    private InteractionEntry GetActiveInteraction()
    {
        foreach (InteractionEntry entry in interactionData)
        {
            if (completedInteractions.Contains(entry)) continue;

            if (entry.condition == ConditionType.RewardItem)
            {
                if (!IsConditionMet(entry)) return null;
                continue;
            }

            if (!IsConditionMet(entry)) continue;

            if (entry.condition == ConditionType.None)
            {
                if (entry.repeatable)
                {
                    bool nextEntryReady = false;

                    for (int i = interactionData.IndexOf(entry) + 1; i < interactionData.Count; i++)
                    {
                        InteractionEntry next = interactionData[i];
                        if (completedInteractions.Contains(next)) continue;

                        if (IsConditionMet(next))
                        {
                            nextEntryReady = true;
                            break;
                        }
                    }

                    if (nextEntryReady) continue;
                    return entry;
                }
                else return entry;
            }
            else
            {
                if (IsConditionMet(entry))
                    return entry;
            }
        }

        return null;
    }

    private void CheckCompletedInteraction()
    {
        // Early exit for regular pickups — no persistent state needed
        foreach (InteractionEntry entry in interactionData)
        {
            if (entry.interact.interactionType == InteractionType.Pickup &&
                !entry.interact.HasType(ItemType.Unique) &&
                !entry.interact.HasType(ItemType.Collectable))
            {
                OnControllerCompleted?.Invoke(this);
                Destroy(gameObject);
                return;
            }
        }

        bool allComplete = true;

        foreach (InteractionEntry entry in interactionData)
        {
            if (!entry.repeatable && !completedInteractions.Contains(entry))
            {
                allComplete = false;
                break;
            }
        }
        if (allComplete)
        {
            WorldInteractable worldInteractable = GetComponent<WorldInteractable>();
            if (worldInteractable != null && !string.IsNullOrEmpty(worldInteractable.worldInteractableID))
                SaveManager.Instance.UpdateInteractionState(worldInteractable.worldInteractableID, interactionData.Count - 1);

            OnControllerCompleted?.Invoke(this);
            Destroy(gameObject);
        }
    }

    private int GetInteractIndex(InteractionEntry entry)
    {
        return interactionData.IndexOf(entry);
    }

    private bool IsConditionMet(InteractionEntry entry)
    {
        if (entry.useVariableLogic && entry.variableConditions != null && entry.variableConditions.Count > 0)
        {
            foreach (var cond in entry.variableConditions)
            {
                if (!CheckVariableCondition(cond)) return false;
            }
        }

        switch (entry.condition)
        {
            case ConditionType.None: return true;
            case ConditionType.RequiresItem:
                if (entry.reqInteract.interactionType == InteractionType.Note)
                    return NoteManager.Instance.journalEntries.Exists(n => n.title == entry.note.data.title);
                return inventory.keyItems.Find(item => item.itemData != null && item.itemData == entry.reqInteract);
            case ConditionType.RequiresDoor: return door.IsOnCorrectSide();
            case ConditionType.RequiresEvent: return eventTriggered;
            case ConditionType.AfterInteraction: return completedInteractions.Contains(interactionData[entry.reqInteractIndex]);
            case ConditionType.RewardItem: return completedInteractions.Contains(interactionData[entry.reqInteractIndex]);
            default: return false;
        }
    }

    private bool CheckVariableCondition(InteractionEntry.InteractionVariableCondition cond)
    {
        int intVal = DialogueVariableManager.Instance.GetInt(cond.intVariable);
        bool boolVal = DialogueVariableManager.Instance.GetBool(cond.boolVariable);

        switch (cond.conditionType)
        {
            case DialogueDataSO.DialogueConditionType.Bool: return boolVal == cond.boolValue;
            case DialogueDataSO.DialogueConditionType.IntEquals: return intVal == cond.intValue;
            case DialogueDataSO.DialogueConditionType.IntGreater: return intVal > cond.intValue;
            case DialogueDataSO.DialogueConditionType.IntLess: return intVal < cond.intValue;
            case DialogueDataSO.DialogueConditionType.IntGreaterOrEqual: return intVal >= cond.intValue;
            case DialogueDataSO.DialogueConditionType.IntLessOrEqual: return intVal <= cond.intValue;
            default: return true;
        }
    }
    #endregion

    #region Interaction Type Handlers
    private void PickUp(InteractionEntry entry)
    {
        InteractableData interactData = entry.interact;
        if (inventory == null || interactData.inventoryItem == null)
        {
            completedInteractions.Add(entry);
            HidePrompt();
            Destroy(gameObject);
            return;
        }

        if (interactData.HasType(ItemType.Unique) || interactData.HasType(ItemType.Collectable) || entry.condition == ConditionType.RewardItem)
        {
            DoActualPickup(entry);
            return;
        }

        if (interactData.inventoryItem != null &&
            interactData.inventoryItem.gameObject.name == "Brick")
        {
            List<GridItem> existingBricks = inventory.items.FindAll(
                item => item.name == interactData.inventoryItem.gameObject.name);
            foreach (GridItem brick in existingBricks)
                inventory.RemoveItem(brick);
            DoActualPickup(entry);
            return;
        }

        bool willCreateNewStack = WillCreateNewStack(interactData);

        if (willCreateNewStack && !PlayerController.Instance.sunburnController.IsPlayerInSun())
        {
            UIManager.Instance.progressionHandler.ShowPickupCheck(
                interactData.interactableName,
                () => { DoActualPickup(entry); },
                () => { RefreshPrompt(); } // Decline: just return to gameplay, leave the pickup in the world
            );
        }
        else
        {
            DoActualPickup(entry);
        }
    }

    private bool WillCreateNewStack(InteractableData interactData)
    {
        if (!interactData.stackable) return true;

        List<GridItem> existingItems = inventory.items.FindAll(
            item => item.itemData == interactData);

        foreach (GridItem existing in existingItems)
        {
            if (existing.itemAmount + interactData.itemAmount <= interactData.stackSize)
                return false;
        }

        return true;
    }
    
    private void DoActualPickup(InteractionEntry entry)
    {
        InteractableData interactData = entry.interact;
        WorldInteractable worldInteractable = GetComponent<WorldInteractable>();

        if (interactData.HasType(ItemType.Unique))
        {
            GridItem keyItem = inventory.CreateInventoryItem(interactData, inventory.keyItemContainer, interactData.itemAmount);
            inventory.AddKeyItem(keyItem);

            if (worldInteractable != null && !string.IsNullOrEmpty(worldInteractable.worldInteractableID))
                SaveManager.Instance.UpdateInteractionState(worldInteractable.worldInteractableID, interactionData.Count - 1);

            UIManager.Instance.itemPickupUI?.ShowKeyItem(true, keyItem.name, keyItem.GetComponentInChildren<Image>().sprite);

            CompletePickup(entry);
            return;
        }

        if (interactData.HasType(ItemType.Collectable))
        {
            if (vidaInventory != null)
            {
                GridInventory.HideyHops++;
                vidaInventory.UpdateHideyHopUI();
                GridItem keyItem = inventory.CreateInventoryItem(interactData, vidaInventory.keyItemContainer, interactData.itemAmount);

                UIManager.Instance.itemPickupUI?.ShowKeyItem(true, keyItem.name, keyItem.GetComponentInChildren<Image>().sprite);

                vidaInventory.AddKeyItem(keyItem);

                if (worldInteractable != null && !string.IsNullOrEmpty(worldInteractable.worldInteractableID))
                    SaveManager.Instance.UpdateInteractionState(worldInteractable.worldInteractableID, interactionData.Count - 1);
            }
            else
            {
                Debug.LogWarning("Vida inventory not assigned!");
            }

            CompletePickup(entry);
            return;
        }

        if (interactData.itemWeaponData != null && interactData.itemWeaponData.weaponType == WeaponType.Throwable)
        {
            Projectile projectileData = interactData.interactablePrefab.GetComponent<Projectile>();
            projectileData.wasFired = false;
            UIManager.Instance.itemPickupUI?.ShowItem(true, interactData.name, interactData.itemAmount);
        }

        if (interactData.stackable)
        {
            List<GridItem> existingItems = inventory.items.FindAll(
                item => item.name == interactData.inventoryItem.gameObject.name);

            bool addedToStack = false;
            for (int i = 0; i < existingItems.Count; i++)
            {
                if (existingItems[i].itemAmount == interactData.stackSize) continue;
                int newAmount = existingItems[i].itemAmount + interactData.itemAmount;
                if (newAmount <= interactData.stackSize)
                {
                    existingItems[i].itemAmount += interactData.itemAmount;
                    existingItems[i].UpdateUI();

                    UIManager.Instance.itemPickupUI?.ShowItem(true, interactData.name, interactData.itemAmount);

                    addedToStack = true;
                    break;
                }
            }

            if (addedToStack)
            {
                if (worldInteractable != null && !string.IsNullOrEmpty(worldInteractable.worldInteractableID))
                    SaveManager.Instance.RegisterWorldItemPickup(worldInteractable.worldInteractableID);

                CompletePickup(entry);
                return;
            }
        }

        GridItem item = inventory.CreateInventoryItem(interactData, inventory.itemContainer, interactData.itemAmount);

        bool added = inventory.TryAddItem(item);
        if (!added)
        {
            Destroy(item.gameObject);
            interactionIndex = GetInteractIndex(entry) - 1;
            return;
        }

        if (worldInteractable != null && !string.IsNullOrEmpty(worldInteractable.worldInteractableID))
            SaveManager.Instance.RegisterWorldItemPickup(worldInteractable.worldInteractableID);

        UIManager.Instance.itemPickupUI?.ShowItem(true, interactData.name, interactData.itemAmount);
        CompletePickup(entry);
    }

    private void CompletePickup(InteractionEntry entry)
    {
        completedInteractions.Add(entry);
        HidePrompt();
        weaponUI.RefreshUI();

        if (!processingRewards)
            CheckForPendingRewards();
        else
            ProcessNextReward();
    }

    public void ForceDirectPickup(InteractableData data)
    {
        if (data == null) return;

        // 1. Handle Unique/Key Items
        if (data.HasType(ItemType.Unique))
        {
            GridItem keyItem = inventory.CreateInventoryItem(data, inventory.keyItemContainer, data.itemAmount);
            inventory.AddKeyItem(keyItem);
            UIManager.Instance.itemPickupUI?.ShowKeyItem(true, keyItem.name, keyItem.GetComponentInChildren<Image>().sprite);
            return;
        }

        // 2. Handle Collectables
        if (data.HasType(ItemType.Collectable))
        {
            GridInventory.HideyHops++;
            vidaInventory.UpdateHideyHopUI();
            GridItem keyItem = inventory.CreateInventoryItem(data, vidaInventory.keyItemContainer, data.itemAmount);
            vidaInventory.AddKeyItem(keyItem);
            UIManager.Instance.itemPickupUI?.ShowKeyItem(true, keyItem.name, keyItem.GetComponentInChildren<Image>().sprite);
            return;
        }

        // 3. Handle Stackables (Ammo, etc.)
        if (data.stackable)
        {
            List<GridItem> existingItems = inventory.items.FindAll(item => item.name == data.inventoryItem.gameObject.name);
            foreach (var existing in existingItems)
            {
                if (existing.itemAmount < data.stackSize)
                {
                    existing.itemAmount += data.itemAmount;
                    existing.UpdateUI();
                    UIManager.Instance.itemPickupUI?.ShowItem(true, data.interactableName, data.itemAmount);
                    return;
                }
            }
        }

        // 4. Handle Standard Grid Items
        GridItem newItem = inventory.CreateInventoryItem(data, inventory.itemContainer, data.itemAmount);

        bool added = inventory.TryAddItem(newItem);
        if (added)
        {
            UIManager.Instance.itemPickupUI?.ShowItem(true, data.interactableName, data.itemAmount);
        }
        else
        {
            Destroy(newItem.gameObject);
        }
    }

    private void Note(InteractionEntry entry)
    {
        NoteManager.Instance.ReadNote(entry);
        interactionIndex = GetInteractIndex(entry);
        completedInteractions.Add(entry);
        WorldInteractable worldInteractable = GetComponent<WorldInteractable>();
        if (worldInteractable != null && !string.IsNullOrEmpty(worldInteractable.worldInteractableID))
            SaveManager.Instance.UpdateInteractionState(worldInteractable.worldInteractableID, interactionIndex);
        HidePrompt();
        RefreshPrompt();
    }

    private void UseDoor(InteractionEntry entry)
    {
        if (entry.condition == ConditionType.RequiresItem)
            entry.condition = ConditionType.None;
        
        door.UseDoor();

        for (int i = 0; i < interactionData.IndexOf(entry); i++)
        {
            if (interactionData[i].repeatable && !completedInteractions.Contains(interactionData[i]))
                completedInteractions.Add(interactionData[i]);
        }
    }

    private void UseDrawer(InteractionEntry entry)
    {
        drawer.UseDrawer();

        for (int i = 0; i < interactionData.IndexOf(entry); i++)
        {
            if (interactionData[i].repeatable && !completedInteractions.Contains(interactionData[i]))
                completedInteractions.Add(interactionData[i]);
        }
    }

    private void Dialogue(InteractionEntry entry)
    {
        if (entry.dialogue == null) return;

        if (entry.condition == ConditionType.RequiresItem)
            entry.condition = ConditionType.None;

        if (entry.useVariableLogic && entry.triggeredVariables != null)
            pendingVariableEntry = entry;

        StartCoroutine(DialogueRoutine(entry));
    }

    private IEnumerator DialogueRoutine(InteractionEntry entry)
    {
        PlayerController.Instance.CloseInventory();
        PlayerController.Instance.SwitchToDialogue();

        // Pre-interaction timeline
        if (entry.preInteractionTimeline != null)
            yield return DialogueManager.Instance.PlayCutscene(entry.preInteractionTimeline);

        waitingDialogueEnd = true;
        interactionIndex = GetInteractIndex(entry);
        DialogueManager.Instance.StartDialogue(entry.dialogue);

        if (!entry.repeatable) completedInteractions.Add(entry);
        HidePrompt();

        for (int i = 0; i < interactionData.IndexOf(entry); i++)
        {
            if (interactionData[i].repeatable && !completedInteractions.Contains(interactionData[i]))
                completedInteractions.Add(interactionData[i]);
        }

        // Wait for dialogue to end (DialogueFinished sets waitingDialogueEnd = false)
        while (waitingDialogueEnd) yield return null;

        // Post-interaction timeline
        if (entry.postInteractionTimeline != null)
            yield return DialogueManager.Instance.PlayCutscene(entry.postInteractionTimeline);
    }

    private void DialogueFinished(DialogueDataSO dialogue)
    {
        if (waitingDialogueEnd)
        {
            if (pendingVariableEntry != null)
            {
                foreach (var effect in pendingVariableEntry.triggeredVariables)
                    effect.Apply();
                pendingVariableEntry = null;
            }

            SaveManager.Instance.CaptureDialogueVariables();

            WorldInteractable worldInteractable = GetComponent<WorldInteractable>();
            if (worldInteractable != null && !string.IsNullOrEmpty(worldInteractable.worldInteractableID))
                SaveManager.Instance.UpdateInteractionState(worldInteractable.worldInteractableID, interactionIndex);

            CheckForPendingRewards();
        }

        waitingDialogueEnd = false;
    }

    private void Move(InteractionEntry entry)
    {
        if (PlayerController.Instance.isDodging) return;

        if (entry.condition == ConditionType.RequiresItem)
        {
            GridItem matchingItem = inventory.keyItems.Find(item =>
                item.itemData != null && item.itemData == entry.reqInteract);
            if (matchingItem != null)
            {
                inventory.RemoveKeyItem(matchingItem);
                UIManager.Instance.itemPickupUI?.UseKeyItem(matchingItem.name);
                entry.condition = ConditionType.None;
            }
        }

        waitingMoveEnd = true;
        MoveObject moveableObject = GetComponent<MoveObject>();
        moveableObject.moveLocked = false;
        moveableObject.MoveThisObject(entry);
    }

    private void MoveFinished()
    {
        if (waitingMoveEnd) RefreshPrompt();
        waitingMoveEnd = false;
    }

    private void MapInteract(InteractionEntry entry)
    {
        UIManager.Instance.itemPickupUI?.ShowMapUpdate();
        MapManager.Instance?.SwapMap();
        completedInteractions.Add(entry);
        HidePrompt();
        Destroy(gameObject);
    }

    private void PolaroidSave(InteractionEntry entry)
    {
        GridItem film = inventory.items.Find(data =>
            data.itemData != null &&
            data.itemData == entry.reqInteract);

        if (film != null)
        {
            film.itemAmount -= 1;
            if (film.itemAmount <= 0) inventory.RemoveItem(film);
            UIManager.Instance.itemPickupUI.ShowItem(false, film.name, 1);
            PolaroidSavePoint polaroid = GetComponent<PolaroidSavePoint>();
            polaroid.ActivatePolaroid();
            HidePrompt();
        }
        else Dialogue(entry); // Play missing film strip dialogue
    }

    private void CheckConfirm(InteractionEntry entry)
    {
        HidePrompt();
        UIManager.Instance.progressionHandler.Show
            (
                () => { completedInteractions.Add(entry); CheckForPendingRewards(); }, // Confirm permanent progression
                () => { RefreshPrompt(); } // Decline, prompt reappears 
            );
    }
    #endregion

    #region Reward Queuing System
    // Enqueue conditional reward items so multi-stage progression rewards 
    // are processed sequentially rather than firing overlapping UI popups simultaneously.
    private void CheckForPendingRewards()
    {
        foreach (InteractionEntry entry in interactionData)
        {
            if (completedInteractions.Contains(entry)) continue;

            if (entry.condition == ConditionType.RewardItem && IsConditionMet(entry))
            {
                if (!pendingRewards.Contains(entry))
                    pendingRewards.Enqueue(entry);
            }
        }
        ProcessNextReward();
    }

    private void ProcessNextReward()
    {
        if (pendingRewards.Count == 0)
        {
            if(GetActiveInteraction() != null)
            {
                processingRewards = false;
                RefreshPrompt();
                return;
            }
            
            CheckCompletedInteraction();
            processingRewards = false;
            return;
        }

        processingRewards = true;
        InteractionEntry reward = pendingRewards.Dequeue();

        if (!completedInteractions.Contains(reward))
            completedInteractions.Add(reward);

        ApplyReward(reward);

        if (reward.interact.interactionType != InteractionType.Dialogue)
            ProcessNextReward();
    }

    private void ApplyReward(InteractionEntry entry)
    {
        if (entry.useVariableLogic && entry.triggeredVariables != null)
        {
            foreach (var effect in entry.triggeredVariables)
            {
                effect.Apply();
            }
        }

        switch (entry.interact.interactionType)
        {
            case InteractionType.Pickup:
                PickUp(entry);
                break;
            case InteractionType.Dialogue:
                Dialogue(entry);
                break;
            case InteractionType.Note:
                Note(entry);
                break;
        }
    }
    #endregion

    #region Prompt & UI Management
    public Vector3 GetPromptPosition()
    {
        Vector3 objectPos = (doorObject != null) ? doorObject.bounds.center : transform.position;
        return objectPos + promptOffset;
    }

    public bool TryAutoInteract() // AUTO DIALOGUE TRIGGER
    {
        if (triggerDialogueOnEnter && PlayerController.Instance.playerInput.enabled)
        {
            InteractionEntry entry = GetActiveInteraction();
            if (entry != null && entry.interact.interactionType == InteractionType.Dialogue)
            {
                Dialogue(entry);
                return true;
            }
        }
        return false;
    }

    public void RefreshPrompt()
    {
        if (this == null) return;

        if (GetActiveInteraction() != null)
            PlayerController.Instance.AddInteraction(this);
    }

    public void HidePrompt()
    {
        PlayerController.Instance.RemoveInteraction(this);
    }
    #endregion
}