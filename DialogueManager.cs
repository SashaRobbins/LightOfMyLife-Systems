using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Playables;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Cinemachine;
using static DialogueDataSO;

public class DialogueManager : MonoBehaviour
{
    public bool isInDialogue = false;

    private DialogueDataSO rootDialogueData;

    [Header("UI References")]
    [SerializeField] private PlayableDirector animDirector;
    [SerializeField] private CameraRegistry camRegistry;
    [SerializeField] private TMP_Text dialogueSpeaker;
    [SerializeField] private TMP_Text dialogueText;
    [SerializeField] private TMP_Text dialogueEndText;
    [SerializeField] private Image dialogueEndImage;
    [SerializeField] private CanvasGroup dialogueCG, choicesCG;
    [SerializeField] private CanvasGroup[] choiceObjects;
    [SerializeField] private GameObject dialogueEndUI;
    private List<TMP_Text> choiceTexts = new List<TMP_Text>();
    private CanvasGroup canvasGroup;
    private PlayerController playerController;
    [SerializeField] private Image nextDialogueImage;
    private Coroutine nextDialogueFadeCoroutine;
    private Coroutine nextDialogueThrob;

    public static System.Action<DialogueDataSO> OnDialogueEnded;
    private bool isQuitting = false;

    [Header("Skip Indicator Settings")]
    [SerializeField] private float skipFadeInDuration = 0.7f;
    [SerializeField] private float skipFadeOutDuration = 0.3f;
    private Coroutine skipFadeCoroutine;

    [Header("Type Settings")]
    [Tooltip("Time in seconds between each character appearing.")]
    [SerializeField] private float typeSpeed = 0.08f;
    private Coroutine typeCoroutine;
    private bool isTyping = false;
    private string fullTextLine;

    [Header("Inputs")]
    private InputAction progressDialogue;
    private InputAction skipDialogue;
    private InputAction choice1;
    private InputAction choice2;
    private InputAction choice3;
    private PlayerInput playerInput;
    private bool inChoice = false;
    private bool inAnimation = false;

    [Header("Inventory Trigger")]
    [SerializeField] private DialogueDataSO triggerDialogueData;
    [SerializeField] private InventoryCursor inventoryCursor;

    private DialogueDataSO currentNodeData;
    private List<DialogueNode> currentConversation;
    private int currentIndex = 0;
    private DialogueChoice[] activeChoices;

    #region Initialization, Singletons & Lifecycle

    private void Start()
    {
        canvasGroup = GetComponent<CanvasGroup>();
        playerController = GameManager.Instance.player.GetComponent<PlayerController>();
        camRegistry = FindFirstObjectByType<CameraRegistry>();
        SetCanvasGroupActive(canvasGroup, false);
        SetCanvasGroupActive(dialogueCG, false);

        for (int i = 0; i < choiceObjects.Length; i++)
        {
            SetCanvasGroupActive(choiceObjects[i], false);
            TMP_Text displayText = choiceObjects[i].GetComponentInChildren<TMP_Text>();
            if (displayText != null) choiceTexts.Add(displayText);
        }

        playerInput = GameManager.Instance.playerInput;
        progressDialogue = playerInput.actions.FindActionMap("Dialogue").FindAction("ProgressDialogue");
        skipDialogue = playerInput.actions.FindActionMap("Dialogue").FindAction("SkipDialogue");
        choice1 = playerInput.actions.FindActionMap("Dialogue").FindAction("Choice1");
        choice2 = playerInput.actions.FindActionMap("Dialogue").FindAction("Choice2");
        choice3 = playerInput.actions.FindActionMap("Dialogue").FindAction("Choice3");

        progressDialogue.started += ProgressDialogue;
        skipDialogue.performed += SkipDialogue;
        skipDialogue.started += SkipDialogue;
        skipDialogue.canceled += SkipCancelled;
        choice1.started += Choice1;
        choice2.started += Choice2;
        choice3.started += Choice3;
    }

    private void OnDisable()
    {
        if (progressDialogue == null) return;
        progressDialogue.started -= ProgressDialogue;
        skipDialogue.performed -= SkipDialogue;
        skipDialogue.started -= SkipDialogue;
        skipDialogue.canceled -= SkipCancelled;
        choice1.started -= Choice1;
        choice2.started -= Choice2;
        choice3.started -= Choice3;
    }

    private void Update()
    {
        if (!isInDialogue || inChoice) return;

        bool skipHeld = skipDialogue.IsPressed();

        if (!skipHeld)
        {
            // Ensure indicator rests at 0.25 when skip isn't held
            SetSkipIndicatorAlphaImmediate(0.25f);
        }
    }

    private void OnApplicationQuit()
    {
        isQuitting = true;
    }

    private void OnDestroy()
    {
        if (isQuitting) return;

        StopAllCoroutines();
        SetCanvasGroupActive(canvasGroup, false);
        SetCanvasGroupActive(dialogueCG, false);
        if (playerController != null) playerController.SwitchToGameplay();
        
        if (animDirector != null)
        {
            animDirector.stopped -= OnAnimationStopped;
            animDirector.Stop();
        }

        isTyping = false;
        OnDialogueEnded?.Invoke(currentNodeData);
        currentConversation = null;
        currentNodeData = null;
    }

    #endregion

    #region Core Dialogue Flow & Display

    public void StartDialogue(DialogueDataSO data)
    {
        rootDialogueData = data;
        currentNodeData = data;

        if (skipFadeCoroutine != null) StopCoroutine(skipFadeCoroutine);
        skipFadeCoroutine = StartCoroutine(FadeSkipIndicator(0.25f));

        if (data != null && (data.conversationNode.Count > 0 || data.nodeType == NodeType.Animated))
        {
            currentConversation = GetValidConversation(data);
            currentIndex = 0;
            isInDialogue = true;

            SetCanvasGroupActive(canvasGroup, true);
            SetCanvasGroupActive(dialogueCG, true);
            playerController.SwitchToDialogue();

            ResetCameras();
            AdvanceDialogue();
            playerController.ToggleUI(false);
        }
        else
        {
            Debug.Log("No dialogue data found");
        }
    }

    public void AdvanceDialogue()
    {
        if (inAnimation) return;

        if (isTyping) // Skips the typewriting effect and displays the whole dialogue line
        {
            CompleteLineInstantly();
            return;
        }

        if (currentIndex < currentConversation.Count)
            DisplayNextLine();
        else
            HandleEndConversation();
    }

    private void DisplayNextLine()
    {
        DialogueNode line = currentConversation[currentIndex];
        dialogueSpeaker.text = line.speaker;
        fullTextLine = line.dialogue;

        // Sets a baseline priority across conversation cameras, 
        // then elevates the active speaker's camera priority to bring them into focus.
        SwitchToCamera(camRegistry.GetCamera(line.speakerCamID));

        if (typeCoroutine != null) StopCoroutine(typeCoroutine);
        typeCoroutine = StartCoroutine(TypeText(fullTextLine));

        currentIndex++;
    }

    private void CompleteLineInstantly()
    {
        if (nextDialogueFadeCoroutine != null) StopCoroutine(nextDialogueFadeCoroutine);
        nextDialogueFadeCoroutine = StartCoroutine(FadeNextDialogueImage(1f, 0.5f));
        nextDialogueThrob = StartCoroutine(ThrobNextDialogueImage());

        if (typeCoroutine != null) StopCoroutine(typeCoroutine);
        dialogueText.text = fullTextLine;
        isTyping = false;
    }

    private void HandleEndConversation()
    {
        if (currentNodeData.nodeType == NodeType.Choice)
            ShowChoices();
        else if (currentNodeData.nodeType == NodeType.Animated)
            PlayAnimation();
        else 
        {
            if (!NextNodeCheck()) EndDialogue(); 
        }
    }

    private bool NextNodeCheck()
    {
        if (currentNodeData != null && currentNodeData.nextNode != null)
        {
            DialogueDataSO targetNode = currentNodeData.nextNode;
            LoadNode(targetNode);
            return true;
        }
        return false;
    }

    private void LoadNode(DialogueDataSO targetNode)
    {
        currentNodeData = targetNode;
        ResetCameras();
        currentConversation = GetValidConversation(targetNode);
        currentIndex = 0;
        AdvanceDialogue();
    }

    public void EndDialogue()
    {
        if (skipFadeCoroutine != null) StopCoroutine(skipFadeCoroutine);
        if (typeCoroutine != null) StopCoroutine(typeCoroutine);
        skipFadeCoroutine = StartCoroutine(FadeSkipIndicator(0f));

        ResetCameras();
        SetCanvasGroupActive(canvasGroup, false);
        SetCanvasGroupActive(dialogueCG, false);
        playerController.ToggleUI(true);

        isInDialogue = false;
        isTyping = false;

        Debug.Log("End of dialogue.");

        if (currentNodeData != null && currentNodeData == triggerDialogueData)
        {
            Debug.Log("Triggering sister inventory from dialogue.");
            // Defers inventory setup by one frame after dialogue ends 
            // to ensure safe state transitions between dialogue and inventory UI maps.
            StartCoroutine(OpenSisterInventoryDelayed());
        }
        else
        {
            playerController.SwitchToGameplay();
        }

        OnDialogueEnded?.Invoke(currentNodeData);
        currentConversation = null;
        currentNodeData = null;
    }

    private IEnumerator OpenSisterInventoryDelayed()
    {
        yield return null;

        inventoryCursor.inventory = inventoryCursor.sisterInventory;
        inventoryCursor.selectPanel = inventoryCursor.sisterSelectPanel;

        inventoryCursor.ForceInventoryUIRefresh();
        inventoryCursor.OnInventoryOpen();

        GameManager.Instance.player.GetComponent<PlayerController>().SwitchToInventory();

        inventoryCursor.OpenInventory();
    }

    #endregion

    #region Typewriter & Visual Polish Coroutines

    private IEnumerator TypeText(string text)
    {
        // Hide the indicator when typing begins
        if (nextDialogueFadeCoroutine != null) StopCoroutine(nextDialogueFadeCoroutine);
        if (nextDialogueThrob != null) { StopCoroutine(nextDialogueThrob); nextDialogueThrob = null; }
        nextDialogueFadeCoroutine = StartCoroutine(FadeNextDialogueImage(0f, 0.25f));

        isTyping = true;
        dialogueText.text = "";

        foreach (char letter in text.ToCharArray())
        {
            dialogueText.text += letter;
            yield return new WaitForSeconds(typeSpeed);
        }

        isTyping = false;

        // Show and throb the indicator when typing finishes
        if (nextDialogueFadeCoroutine != null) StopCoroutine(nextDialogueFadeCoroutine);
        nextDialogueFadeCoroutine = StartCoroutine(FadeNextDialogueImage(1f, 0.5f));
        nextDialogueThrob = StartCoroutine(ThrobNextDialogueImage());
    }

    private IEnumerator FadeNextDialogueImage(float targetAlpha, float duration)
    {
        if (nextDialogueImage == null) yield break;
        float startAlpha = nextDialogueImage.color.a;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            Color c = nextDialogueImage.color;
            c.a = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
            nextDialogueImage.color = c;
            yield return null;
        }

        Color final = nextDialogueImage.color;
        final.a = targetAlpha;
        nextDialogueImage.color = final;

        if (targetAlpha <= 0f && nextDialogueThrob != null)
        {
            StopCoroutine(nextDialogueThrob);
            nextDialogueThrob = null;
            nextDialogueImage.transform.localScale = Vector3.one * 0.1f;
        }
    }

    private IEnumerator ThrobNextDialogueImage()
    {
        if (nextDialogueImage == null) yield break;
        while (true)
        {
            yield return ScaleNextDialogueImage(0.1f, 0.25f, 0.7f);
            yield return ScaleNextDialogueImage(0.25f, 0.1f, 0.7f);
        }
    }

    private IEnumerator ScaleNextDialogueImage(float from, float to, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float scale = Mathf.Lerp(from, to, elapsed / duration);
            nextDialogueImage.transform.localScale = Vector3.one * scale;
            yield return null;
        }
        nextDialogueImage.transform.localScale = Vector3.one * to;
    }

    private IEnumerator FadeSkipIndicator(float targetAlpha)
    {
        float duration = targetAlpha >= 1f ? skipFadeInDuration : skipFadeOutDuration;
        float startTextAlpha = dialogueEndText != null ? dialogueEndText.color.a : 0.25f;
        float startImageAlpha = dialogueEndImage != null ? dialogueEndImage.color.a : 0.25f;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            if (dialogueEndText != null)
            {
                Color c = dialogueEndText.color;
                c.a = Mathf.Lerp(startTextAlpha, targetAlpha, t);
                dialogueEndText.color = c;
            }

            if (dialogueEndImage != null)
            {
                Color c = dialogueEndImage.color;
                c.a = Mathf.Lerp(startImageAlpha, targetAlpha, t);
                dialogueEndImage.color = c;
            }

            yield return null;
        }
    }

    private void SetSkipIndicatorAlphaImmediate(float alpha)
    {
        if (dialogueEndText != null)
        {
            Color c = dialogueEndText.color;
            if (Mathf.Approximately(c.a, alpha)) return;
            c.a = alpha;
            dialogueEndText.color = c;
        }

        if (dialogueEndImage != null)
        {
            Color c = dialogueEndImage.color;
            c.a = alpha;
            dialogueEndImage.color = c;
        }
    }

    private void SetCanvasGroupActive(CanvasGroup cg, bool active)
    {
        cg.alpha = active ? 1 : 0;
        cg.interactable = active;
        cg.blocksRaycasts = active;
    }

    public void SkipOpacityChange()
    {
        if (skipFadeCoroutine != null) StopCoroutine(skipFadeCoroutine);
        skipFadeCoroutine = StartCoroutine(FadeSkipIndicator(1f));
    }

    public void ResetSkipOpacity()
    {
        if (skipFadeCoroutine != null) StopCoroutine(skipFadeCoroutine);
        skipFadeCoroutine = StartCoroutine(FadeSkipIndicator(0.25f));
    }

    #endregion

    #region Choices & Variable Evaluation

    private void ProgressDialogue(InputAction.CallbackContext obj)
    {
        if (inChoice) return;
        AdvanceDialogue();
    }

    private void SkipDialogue(InputAction.CallbackContext obj)
    {
        if (inChoice) return;
        if (obj.started) SkipOpacityChange();
        if (obj.performed) 
        {
            if (inAnimation) 
            { 
                OnAnimationSkipped();
                return;
            }

            if (currentNodeData.nodeType == NodeType.Choice)
            {
                currentIndex = currentConversation.Count - 1;
                DisplayNextLine();
                CompleteLineInstantly();
                AdvanceDialogue();
            }
            else 
            { 
                if (!NextNodeCheck()) EndDialogue(); 
            }
        }
    }

    private void SkipCancelled(InputAction.CallbackContext obj)
    {
        if (inChoice) return;
        ResetSkipOpacity();
    }

    private void Choice1(InputAction.CallbackContext obj)
    {
        if (!inChoice) return;
        SelectChoice(0);
    }

    private void Choice2(InputAction.CallbackContext obj)
    {
        if (!inChoice) return;
        SelectChoice(1);
    }

    private void Choice3(InputAction.CallbackContext obj)
    {
        if (!inChoice) return;
        SelectChoice(2);
    }

    private void SelectChoice(int index)
    {
        if (activeChoices == null || index >= activeChoices.Length) return;

        DialogueChoice choice = activeChoices[index];

        if (choice.effects != null)
        {
            foreach (var effect in choice.effects)
            {
                if (effect == null) continue;
                effect.Apply();
            }
        }

        DialogueDataSO targetNode = choice.targetNode;
        HideChoices();
        LoadNode(targetNode);
    }

    // Evaluates global dialogue variables against conditional rules 
    // to dynamically branch conversation lines or choice arrays.
    private List<DialogueNode> GetValidConversation(DialogueDataSO data)
    {
        if (data.conditionalConversations != null)
        {
            foreach (var condition in data.conditionalConversations)
            {
                int value = DialogueVariableManager.Instance.GetInt(condition.intVariable);
                bool boolVal = DialogueVariableManager.Instance.GetBool(condition.boolVariable);

                switch (condition.conditionType)
                {
                    case DialogueConditionType.Bool:
                        if (boolVal == condition.boolValue) 
                            return condition.conversation;
                        break;
                    case DialogueConditionType.IntEquals:
                        if (value == condition.intValue) 
                            return condition.conversation;
                        break;
                    case DialogueConditionType.IntGreater:
                        if (value > condition.intValue) 
                            return condition.conversation;
                        break;
                    case DialogueConditionType.IntLess:
                        if (value < condition.intValue) 
                            return condition.conversation;
                        break;
                    case DialogueConditionType.IntGreaterOrEqual:
                        if (value >= condition.intValue) 
                            return condition.conversation;
                        break;
                    case DialogueConditionType.IntLessOrEqual:
                        if (value <= condition.intValue) 
                            return condition.conversation;
                        break;
                }
            }
        }

        return data.conversationNode;
    }

    private DialogueChoice[] GetValidChoices(DialogueDataSO data)
    {
        if (data.conditionalChoices != null)
        {
            foreach (var condition in data.conditionalChoices)
            {
                int value = DialogueVariableManager.Instance.GetInt(condition.intVariable);
                bool boolVal = DialogueVariableManager.Instance.GetBool(condition.boolVariable);

                switch (condition.conditionType)
                {
                    case DialogueConditionType.Bool:
                        if (boolVal == condition.boolValue)
                            return condition.choices;
                        break;
                    case DialogueConditionType.IntEquals:
                        if (value == condition.intValue)
                            return condition.choices;
                        break;
                    case DialogueConditionType.IntGreater:
                        if (value > condition.intValue)
                            return condition.choices;
                        break;
                    case DialogueConditionType.IntLess:
                        if (value < condition.intValue)
                            return condition.choices;
                        break;
                    case DialogueConditionType.IntGreaterOrEqual:
                        if (value >= condition.intValue)
                            return condition.choices;
                        break;
                    case DialogueConditionType.IntLessOrEqual:
                        if (value <= condition.intValue)
                            return condition.choices;
                        break;
                }
            }
        }

        return data.dialogueChoices;
    }

    private void ShowChoices()
    {
        SetCanvasGroupActive(choicesCG, true);
        if (skipFadeCoroutine != null) StopCoroutine(skipFadeCoroutine);
        SetSkipIndicatorAlphaImmediate(0.25f);

        activeChoices = GetValidChoices(currentNodeData);

        for (int i = 0; i < activeChoices.Length; i++)
        {
            SetCanvasGroupActive(choiceObjects[i], true);
            choiceTexts[i].text = activeChoices[i].displayText;
        }

        inChoice = true;

        if (dialogueEndUI != null) dialogueEndUI.SetActive(false);
    }

    private void HideChoices()
    {
        SetCanvasGroupActive(choicesCG, false);
        foreach(CanvasGroup choice in choiceObjects) SetCanvasGroupActive(choice, false);
        foreach (TMP_Text text in choiceTexts) text.text = "";
        inChoice = false;
        if (dialogueEndUI != null) dialogueEndUI.SetActive(true);
    }

    #endregion

    #region Cutscene & Animation Management

    private void PlayAnimation()
    {
        SetCanvasGroupActive(dialogueCG, false);
        animDirector.playableAsset = currentNodeData.animationScene;
        animDirector.stopped += OnAnimationStopped;
        animDirector.Play();
        inAnimation = true;
    }

    private void OnAnimationSkipped()
    {
        if (animDirector != null)
        {
            animDirector.time = animDirector.duration;
            animDirector.Evaluate();
            animDirector.Stop();
            animDirector.stopped -= OnAnimationStopped;
        }

        inAnimation = false;
        SetCanvasGroupActive(dialogueCG, true);
        if (!NextNodeCheck()) EndDialogue();
    }

    private void OnAnimationStopped(PlayableDirector director)
    {
        inAnimation = false;
        SetCanvasGroupActive(dialogueCG, true);
        director.stopped -= OnAnimationStopped;
        if (!NextNodeCheck()) EndDialogue();
    }

    #endregion

    #region Camera Framing

    private void ResetCameras()
    {
        if (currentConversation == null) return;
        foreach (var line in currentConversation)
        {
            CinemachineCamera speakerCam = camRegistry.GetCamera(line.speakerCamID);
            if (speakerCam != null)
            {
                speakerCam.Priority = -5;
            }
        }
    }

    private void SwitchToCamera(CinemachineCamera targetCamera)
    {
        if (currentConversation == null) return;
        
        foreach (var line in currentConversation)
        {
            CinemachineCamera speakerCam = camRegistry.GetCamera(line.speakerCamID);
            if (speakerCam != null)
            {
                speakerCam.Priority = 10;
            }
        }

        if (targetCamera != null)
        {
            targetCamera.Priority = 20;
        }
    }

    #endregion
}