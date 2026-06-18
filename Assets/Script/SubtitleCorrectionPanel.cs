using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace VerbalProcess
{
    public class SubtitleCorrectionPanel : MonoBehaviour
    {
        [Header("UI Containers")]
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Transform chipsContainer;
        [SerializeField] private TextMeshProUGUI guideText;

        [Header("Prefabs")]
        [SerializeField] private WordChip wordChipPrefab;

        [Header("Buttons")]
        [SerializeField] private Button btnSendAnyway;
        [SerializeField] private Button btnDiscard;
        [SerializeField] private Button btnReSpeak;

        [Header("Settings")]
        [SerializeField] private float lowConfidenceThreshold = 0.75f;
        [SerializeField] private WordLayout wordLayout;

        private List<WordChip> _activeChips = new List<WordChip>();
        private STTManager.CorrectionRequestMessage _currentMessage;

        // 드래그 상태 관리
        private bool _isDragging = false;
        private int _dragStartIdx = -1;
        private int _selectedStartIdx = -1;
        private int _selectedEndIdx = -1;

        // 콜백
        private Action _onSendAnyway;
        private Action _onDiscard;
        private Action<int, int, string[]> _onReSpeak;

        private void Awake()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);

            if (btnSendAnyway != null)
                btnSendAnyway.onClick.AddListener(HandleSendAnyway);

            if (btnDiscard != null)
                btnDiscard.onClick.AddListener(HandleDiscard);

            if (btnReSpeak != null)
                btnReSpeak.onClick.AddListener(HandleReSpeak);
        }

        public void Open(
            STTManager.CorrectionRequestMessage msg,
            Action onSendAnyway,
            Action onDiscard,
            Action<int, int, string[]> onReSpeak)
        {
            _currentMessage = msg;
            _onSendAnyway = onSendAnyway;
            _onDiscard = onDiscard;
            _onReSpeak = onReSpeak;

            _selectedStartIdx = -1;
            _selectedEndIdx = -1;

            if (guideText != null)
                guideText.text = "교정할 단어들을 드래그로 선택한 뒤, 아래 [재발화]를 눌러 다시 말해 주세요.";

            // 칩 생성 및 가로 너비 측정이 정상적으로 동작하려면 부모 UI 오브젝트들이 모두 활성화(Active)된 상태여야 합니다.
            if (panelRoot != null)
                panelRoot.SetActive(true);

            // 1. 기존 단어 칩 제거
            ClearChips();

            // WordLayout의 크기와 정렬 좌표를 명시적으로 초기화합니다.
            if (wordLayout != null)
            {
                wordLayout.Initialize();
            }

            // 2. 단어 칩 생성
            if (msg.words != null && wordChipPrefab != null && chipsContainer != null)
            {
                for (int i = 0; i < msg.words.Length; i++)
                {
                    string word = msg.words[i];
                    float conf = (msg.word_confidences != null && i < msg.word_confidences.Length) 
                        ? msg.word_confidences[i] 
                        : 1.0f;

                    WordChip chip = Instantiate(wordChipPrefab, chipsContainer);
                    chip.Setup(this, i, word, conf, lowConfidenceThreshold);
                    _activeChips.Add(chip);
                }

                // Setup을 통해 각 칩의 텍스트가 채워진 상태에서, RectTransform 크기가 즉시 갱신되도록 캔버스 업데이트를 강제합니다.
                Canvas.ForceUpdateCanvases();

                // 텍스트 크기 측정이 보장된 상태에서 각 단어 칩을 레이아웃에 순서대로 추가 배치합니다.
                if (wordLayout != null)
                {
                    foreach (var chip in _activeChips)
                    {
                        wordLayout.AddChips(chip);
                    }
                }
            }

            UpdateButtonStates();
        }

        public void Close()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);

            ClearChips();
            wordLayout.ClearChips();
        }

        private void ClearChips()
        {
            foreach (var chip in _activeChips)
            {
                if (chip != null)
                    Destroy(chip.gameObject);
            }
            _activeChips.Clear();
        }

        private void UpdateButtonStates()
        {
            // 단어 칩이 최소 하나라도 선택되어 있을 때만 '재발화' 버튼 활성화
            if (btnReSpeak != null)
            {
                btnReSpeak.interactable = (_selectedStartIdx != -1 && _selectedEndIdx != -1);
            }
        }

        // ==================== 드래그/선택 이벤트 수신 ====================

        public void OnChipPointerDown(WordChip clickedChip)
        {
            _isDragging = true;
            _dragStartIdx = clickedChip.Index;
            UpdateSelectionRange(_dragStartIdx, _dragStartIdx);
        }

        public void OnChipPointerEnter(WordChip hoveredChip)
        {
            if (!_isDragging) return;
            UpdateSelectionRange(_dragStartIdx, hoveredChip.Index);
        }

        public void OnChipPointerUp(WordChip releasedChip)
        {
            _isDragging = false;
            UpdateButtonStates();
        }

        private void UpdateSelectionRange(int start, int end)
        {
            _selectedStartIdx = Mathf.Min(start, end);
            _selectedEndIdx = Mathf.Max(start, end);

            for (int i = 0; i < _activeChips.Count; i++)
            {
                bool inRange = (i >= _selectedStartIdx && i <= _selectedEndIdx);
                _activeChips[i].SetSelected(inRange);
            }
        }

        // ==================== 버튼 클릭 핸들러 ====================

        private void HandleSendAnyway()
        {
            _onSendAnyway?.Invoke();
            Close();
        }

        private void HandleDiscard()
        {
            _onDiscard?.Invoke();
            Close();
        }

        private void HandleReSpeak()
        {
            if (_selectedStartIdx == -1 || _selectedEndIdx == -1 || _currentMessage == null) return;
            
            // 재발화 콜백 실행 (인덱스 범위 및 원본 단어 목록 전달)
            _onReSpeak?.Invoke(_selectedStartIdx, _selectedEndIdx, _currentMessage.words);
            
            // Re-speak 대기 상태임을 UI에 표시
            if (guideText != null)
            {
                guideText.text = "<color=red>● 재발화 대기 중...</color> 선택한 부분을 마이크에 다시 말씀해 주세요.";
            }

            if (btnReSpeak != null) btnReSpeak.interactable = false;
            if (btnSendAnyway != null) btnSendAnyway.interactable = false;
            if (btnDiscard != null) btnDiscard.interactable = false;
        }

        // 재발화 전송이 성공적으로 접수되어 교정 창이 닫힐 때 등을 위한 복구 메서드
        public void ResetButtonInteractions()
        {
            if (btnSendAnyway != null) btnSendAnyway.interactable = true;
            if (btnDiscard != null) btnDiscard.interactable = true;
            UpdateButtonStates();
        }
    }
}
