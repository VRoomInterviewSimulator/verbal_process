using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace VerbalProcess
{
    public class WordChip : MonoBehaviour, IPointerDownHandler, IPointerEnterHandler, IPointerUpHandler
    {
        [Header("UI References")]
        [SerializeField] private TextMeshProUGUI wordText;
        [SerializeField] private Image background;

        [Header("Colors")]
        [SerializeField] private Color normalColor = Color.white;
        [SerializeField] private Color selectedColor = new Color(0.2f, 0.6f, 1.0f, 1.0f); // 파랑
        [SerializeField] private Color lowConfidenceColor = new Color(1.0f, 0.3f, 0.3f, 0.3f); // 붉은 투명 배경
        [SerializeField] private Color lowConfidenceSelectedColor = new Color(1.0f, 0.5f, 0.2f, 1.0f); // 주황

        public int Index { get; private set; }
        public string Word { get; private set; }
        public float Confidence { get; private set; }
        public bool IsSelected { get; private set; }

        private SubtitleCorrectionPanel _panel;
        private bool _isLowConfidence = false;

        public void Setup(SubtitleCorrectionPanel panel, int index, string word, float confidence, float lowConfidenceThreshold = 0.6f)
        {
            _panel = panel;
            Index = index;
            Word = word;
            Confidence = confidence;
            IsSelected = false;

            if (wordText != null)
                wordText.text = word;

            _isLowConfidence = confidence < lowConfidenceThreshold;
            UpdateVisuals();
        }

        public void SetSelected(bool isSelected)
        {
            IsSelected = isSelected;
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            if (background == null) return;

            if (IsSelected)
            {
                background.color = _isLowConfidence ? lowConfidenceSelectedColor : selectedColor;
                if (wordText != null) wordText.color = Color.white;
            }
            else
            {
                background.color = _isLowConfidence ? lowConfidenceColor : normalColor;
                if (wordText != null) wordText.color = _isLowConfidence ? Color.red : Color.black;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_panel != null)
            {
                _panel.OnChipPointerDown(this);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_panel != null)
            {
                _panel.OnChipPointerEnter(this);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_panel != null)
            {
                _panel.OnChipPointerUp(this);
            }
        }
    }
}
