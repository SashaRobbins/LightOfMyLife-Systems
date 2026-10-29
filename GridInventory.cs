using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;

public class GridInventory : MonoBehaviour
{
    public ItemRegistry itemRegistry;
    public ItemSelectPanel selectPanel;
    public GameObject keyItemDisplayPrefab;
    public RecipeDatabase recipeBook;
    public bool isOpen = false;
    public int width, height;
    public float cellSize;
    public float cellSpacing;
    public GridItem itemPickedUp;
    public Transform grid;
    public RectTransform itemContainer;
    public RectTransform keyItemContainer;
    public ScrollRect keyItemSR;
    public CellSlot[,] gridMatrix;
    private TMP_Text hideyHopText;
    private WeaponUI weaponUI;
    private WeaponController weaponController;

    public List<CellSlot> cells = new List<CellSlot>();
    public List<GridItem> items = new List<GridItem>();
    public List<GridItem> keyItems = new List<GridItem>();

    public static int HideyHops = 0;
    private const int MAXHIDEYHOPS = 10;
    private bool loadedFromSave = false;
    public bool loadCompleted = false;

    #region Initialization & Setup
    private void Awake()
    {
        itemRegistry = Resources.Load<ItemRegistry>("ItemLibrary");
        gridMatrix = new CellSlot[width, height];
        for (int i = 0; i < grid.childCount; i++)
        {
            CellSlot cell = grid.GetChild(i).GetComponent<CellSlot>();
            int x = i % width;
            int y = i / width;
            cell.x = x;
            cell.y = y;
            cell.name = "Cell[" + x + "," + y + "]";
            gridMatrix[x, y] = cell;
            cells.Add(cell);
        }

        GridLayoutGroup gridLayout = grid.GetComponent<GridLayoutGroup>();
        if (gridLayout != null)
        {
            cellSize = gridLayout.cellSize.x;
            cellSpacing = gridLayout.spacing.x;
        }
    }

    private void Start()
    {
        weaponUI = FindFirstObjectByType<WeaponUI>();
        weaponController = FindFirstObjectByType<WeaponController>();
        StartCoroutine(InitInventoryItems());

        foreach (Transform child in transform)
        {
            if (child.CompareTag("HideyHop"))
                hideyHopText = child.GetComponent<TMP_Text>();
        }

        UpdateHideyHopUI();
    }

    private void ClearInventory()
    {
        foreach (var cell in cells)
        {
            if (cell == null) continue;
            cell.SetEmpty();
        }

        foreach (var item in items)
        {
            if (item == null) continue;
            item.oldCells.Clear();
        }

        foreach (Transform child in itemContainer)
        {
            if (child != null)
                Destroy(child.gameObject);
        }

        foreach (Transform child in keyItemContainer)
        {
            if (child != null)
                Destroy(child.gameObject);
        }

        items.Clear();
        keyItems.Clear();

        itemPickedUp = null;
    }
    #endregion

    #region Save & Load Serialization
    public List<SerializeableItemEntry> GetSaveSnapshot(List<GridItem> inventoryContainer)
    {
        List<SerializeableItemEntry> savedItems = new List<SerializeableItemEntry>();
        foreach (GridItem item in inventoryContainer)
        {
            if (string.IsNullOrEmpty(item.itemData.saveID))
            {
                Debug.LogError($"{item} saveID is null or empty");
                continue;
            }

            SerializeableItemEntry saveItem = new SerializeableItemEntry();
            saveItem.saveID = item.itemData.saveID;
            saveItem.itemCount = item.itemAmount;
            saveItem.rotated = item.isRotated;
            saveItem.gridPos = item.GetAnchorCellPos();
            savedItems.Add(saveItem);
        }

        return savedItems;
    }

    public void LoadFromSaveData(List<SerializeableItemEntry> itemEntries, List<SerializeableItemEntry> keyItemEntries)
    {
        ClearInventory();
        loadedFromSave = true;
        StartCoroutine(InitSaveItems(itemEntries, keyItemEntries));
    }

    private IEnumerator InitSaveItems(List<SerializeableItemEntry> itemEntries, List<SerializeableItemEntry> keyItemEntries)
    {
        loadCompleted = false;
        // Brief delay allows the UI layout group components to fully initialize and compute cell positions 
        // before items are mathematically placed onto the grid matrix.
        yield return new WaitForSecondsRealtime(0.2f);

        foreach (SerializeableItemEntry entry in itemEntries)
        {
            GridItem savedItem = itemRegistry.GetItemBySaveID(entry.saveID);
            if (savedItem != null)
            {
                GridItem item = CreateInventoryItem(savedItem.itemData, itemContainer, entry.itemCount);
                item.isRotated = entry.rotated;
                item.RefreshSize();
                
                PlaceItemAt(entry.gridPos.x, entry.gridPos.y, item);
                
                if (weaponUI != null) weaponUI.RefreshUI();
                if (weaponController != null) weaponController.CheckAmmo();

                // Brief delay between placements to allow the grid to update between items
                yield return new WaitForSecondsRealtime(0.2f);
            }
        }

        foreach (SerializeableItemEntry entry in keyItemEntries)
        {
            GridItem savedKeyItem = itemRegistry.GetItemBySaveID(entry.saveID);
            if (savedKeyItem != null)
            {
                GridItem item = CreateInventoryItem(savedKeyItem.itemData, keyItemContainer, entry.itemCount);
                AddKeyItem(item);
                yield return new WaitForSecondsRealtime(0.2f);
            }
        }

        if (weaponUI != null) weaponUI.RefreshUI();
        loadCompleted = true;
        yield break;
    }

    private IEnumerator InitInventoryItems()
    {
        loadCompleted = false;
        // Brief delay allows the UI layout group components to fully initialize and compute cell positions 
        // before items are mathematically placed onto the grid matrix.
        yield return new WaitForSecondsRealtime(0.2f);
        
        if (loadedFromSave || items.Count > 0) yield break;

        foreach (Transform child in itemContainer.transform)
        {
            GridItem item = child.GetComponent<GridItem>();
            if (item != null)
            {
                item.inventory = this;
                item.ID = Mathf.RoundToInt(Random.Range(1, 100000));
                item.placedRotationState = false;
                item.UpdateUI();
                TryAddItem(item);
                if (weaponUI != null) weaponUI.RefreshUI();
                if (weaponController != null) weaponController.CheckAmmo();
                // Brief delay between placements to allow the grid to update between items
                yield return new WaitForSecondsRealtime(0.2f);
            }
        }
        if (weaponUI != null) weaponUI.RefreshUI();
        loadCompleted = true;
        yield break;
    }
    #endregion

    #region Spatial Grid & Placement Logic
    public CellSlot GetCellAtPos(int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return null;
        return gridMatrix[x, y];
    }

    public bool TryAddItem(GridItem item)
    {
        if (SearchForSpace(item, item.itemWidth, item.itemHeight, false))
        {
            if (weaponUI != null) weaponUI.RefreshUI();
            return true;
        }

        if (item.canRotate)
        {
            if (SearchForSpace(item, item.itemHeight, item.itemWidth, true))
            {
                if (weaponUI != null) weaponUI.RefreshUI();
                return true;
            }
        }
        UIManager.Instance.itemPickupUI?.ShowInventoryFull();
        return false;
    }

    private bool SearchForSpace(GridItem item, int itemWidth, int itemHeight, bool isRotated)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (CanPlaceAt(x, y, itemWidth, itemHeight))
                {
                    item.isRotated = isRotated;
                    item.placedRotationState = isRotated;

                    PlaceItemAt(x, y, item);
                    return true;
                }
            }
        }
        return false;
    }

    public bool CanPlaceAt(int startX, int startY, int width, int height)
    {
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                CellSlot cell = GetCellAtPos(startX + x, startY + y);
                if (cell == null || cell.fill) return false;
            }
        }
        return true;
    }

    private void PlaceItemAt(int startX, int startY, GridItem item)
    {
        if (item.inventory == null) item.inventory = this;
        if (item.ID == 0) item.ID = Mathf.RoundToInt(Random.Range(1, 100000));
        CellSlot anchorCell = GetCellAtPos(startX, startY);

        item.transform.SetParent(itemContainer);

        RectTransform itemRect = item.GetComponent<RectTransform>();
        // Anchor item rect to top-left (0, 1) so its local position lines up cleanly 
        // with the target cell grid origin point.
        itemRect.pivot = new Vector2(0, 1);
        itemRect.position = anchorCell.rectTransform.position;
        item.currentAnchoredPos = itemRect.anchoredPosition;

        Vector2Int currentSize = item.GetCurrentDimensions();
        float width = cellSize * item.itemWidth + cellSpacing * (currentSize.x - 1);
        float height = cellSize * item.itemHeight + cellSpacing * (currentSize.y - 1);
        itemRect.sizeDelta = new Vector2(width, height);

        for (int x = 0; x < currentSize.x; x++)
        {
            for (int y = 0; y < currentSize.y; y++)
            {
                CellSlot cell = GetCellAtPos(startX + x, startY + y);
                if (cell != null)
                {
                    cell.SetFilled(item.ID);
                    item.oldCells.Add(cell);
                }
            }
        }

        item.isInInventory = true;
        item.RefreshSize();
        AddItem(item);
        if (item.itemData.HasType(ItemType.Weapon)) WeaponAdded(item);
    }
    #endregion

    #region Item Management & State Actions
    public GridItem CreateInventoryItem(InteractableData itemData, Transform parent, int amount)
    {
        GridItem inventoryItem = Instantiate(itemData.inventoryItem, parent);
        inventoryItem.name = itemData.name;
        inventoryItem.inventory = this;
        inventoryItem.objectPrefab = itemData.interactablePrefab;
        inventoryItem.itemAmount = amount;
        inventoryItem.UpdateUI();

        return inventoryItem;
    }

    private void WeaponAdded(GridItem item)
    {
        // If a weapon is added to the inventory, update ammo display if its equipped
        WeaponData targetData = item.itemData.itemWeaponData;

        WeaponController match = PlayerController.Instance.weaponHold
            .GetComponentsInChildren<WeaponController>(true)
            .FirstOrDefault(wC => wC.weaponData == targetData);

        if (match != null) match.CheckAmmo();
    }

    public void AddItem(GridItem item)
    {
        if (!items.Contains(item))
        {
            item.isInInventory = true;
            items.Add(item);
        }
    }

    public void RemoveItem(GridItem item)
    {
        foreach (var cell in item.oldCells)
        {
            if (cell.FID == item.ID) cell.SetEmpty();
        }

        items.Remove(item);

        if (item.itemData.itemWeaponData != null &&
            PlayerController.Instance.currentWeapon != null &&
            PlayerController.Instance.currentWeapon.weaponData == item.itemData.itemWeaponData)
        {
            PlayerController.Instance.weaponManager.TrySwitchAwayFromCurrent();
        }

        Destroy(item.gameObject);
    }

    public void AddKeyItem(GridItem item)
    {
        if (keyItems.Contains(item)) return;
        keyItems.Add(item);
        GameObject key = Instantiate(keyItemDisplayPrefab, keyItemContainer);
        key.GetComponent<KeyItemDisplay>().Setup(item);

        if (loadCompleted) SaveManager.Instance.UpdateKeyItem(this);
    }

    public void RemoveKeyItem(GridItem item)
    {
        keyItems.Remove(item);

        KeyItemDisplay display = keyItemContainer
            .GetComponentsInChildren<KeyItemDisplay>()
            .FirstOrDefault(d => d.linkedItem == item);

        if (display != null) Destroy(display.gameObject);

        Destroy(item.gameObject);
        SaveManager.Instance.UpdateKeyItem(this);
    }

    public bool TryTransferItem(GridItem item, GridInventory targetInventory)
    {
        if (item == null || targetInventory == null) return false;

        foreach (var cell in item.oldCells)
            cell.SetEmpty();

        item.oldCells.Clear();
        items.Remove(item);
        item.inventory = targetInventory;

        bool canFit = targetInventory.TryAddItem(item);
        if (!canFit)
        {
            Debug.Log("Transfer failed - no space");
            UIManager.Instance.itemPickupUI?.ShowInventoryFull();
            return false;
        }

        item.transform.SetParent(targetInventory.itemContainer);

        return true;
    }
    #endregion

    #region Input Handlers & UI Updates
    public void HandlePickupInput(InputAction.CallbackContext obj)
    {
        if (!isOpen) return;
        if (selectPanel.panelOpen) return;

        if (itemPickedUp != null)
        {
            itemPickedUp.TryPlace();
            return;
        }

        GridItem hoveredItem = items.FirstOrDefault(item => item.IsCursorOverItem());
        if (hoveredItem != null)
            hoveredItem.BeginPickup();
    }

    public void HandleRotateInput(InputAction.CallbackContext obj)
    {
        if (!isOpen) return;
        if (itemPickedUp == null) return;

        itemPickedUp.HandleRotate();
    }

    public void UpdateHideyHopUI()
    {
        if (hideyHopText == null) return;
        int current = HideyHops;

        hideyHopText.text = current + " / " + MAXHIDEYHOPS + "\nHidey Hops Found";
    }
    #endregion
}