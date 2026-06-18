using System;
using System.Collections.Generic;
using UnityEngine;
using VerbalProcess;

namespace VerbalProcess
{
    [RequireComponent(typeof(RectTransform))]
    public class WordLayout : MonoBehaviour
    {
        [SerializeField] private float verticalGap = 50;
        [SerializeField] private float horizontalGap = 10;
        [SerializeField] private float leftPadding = 0; 
        [SerializeField] private float rightPadding = 0; 
        [SerializeField] private float verticalPadding = 0;

        private Vector2 postionToInstantiate;
        private float layoutWidth;
        private RectTransform rectTransform;
        private float initialHeight;

        private void Start()
        {
            rectTransform = GetComponent<RectTransform>();
            initialHeight = rectTransform.rect.height; // 에디터 상에 설정된 기본 높이를 기억합니다.
            Initialize();
        }

        // 패널이 열리거나 칩을 배치하기 전에 명시적으로 호출하여 크기와 위치를 초기화합니다.
        public void Initialize()
        {
            if (rectTransform == null)
                rectTransform = GetComponent<RectTransform>();

            // UI 좌표계상 아래로 내려갈수록 Y좌표는 마이너스(-) 방향이 되므로 verticalPadding을 빼줍니다.
            postionToInstantiate = new Vector2(leftPadding, -verticalPadding);

            // 칩이 초기화되면 높이도 에디터 상의 원래 기본 높이로 복원해 줍니다.
            if (initialHeight > 0)
            {
                rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, initialHeight);
            }
        }

        public void AddChips(WordChip chip)
        {
            layoutWidth = rectTransform.rect.width; // Start에는 넣으면 안됨
            RectTransform chipRect = chip.GetComponent<RectTransform>();
            float chipWidth = chipRect.rect.width;

            // 다음 배치될 위치의 오른쪽 끝이 (전체너비 - 우측패딩)을 넘으면 줄바꿈을 수행합니다.
            // (가로 판단을 위해 verticalGap 대신 horizontalGap 또는 0을 사용하여 조건 검사)
            if ((postionToInstantiate.x + chipWidth) > (layoutWidth - rightPadding))
            {
                postionToInstantiate.x = leftPadding;
                postionToInstantiate.y -= verticalGap; // UI 좌표계 기준 아래로 내리기 위해 마이너스(-) 연산
            }

            // 먼저 올바르게 결정된 위치에 칩을 배치합니다.
            chipRect.localPosition = postionToInstantiate;

            // 배치한 이후에 다음 칩을 배치할 X좌표를 누적 계산합니다.
            postionToInstantiate.x += (chipWidth + horizontalGap);
        }

        // 단어 칩이 추가되어 실제 배치 영역이 Rect의 높이보다 커지면 세로 크기를 갱신합니다.
        public void updateheight()
        {
            // postionToInstantiate.y는 음수이므로 절대값으로 바꾼 뒤, 
            // 마지막 라인의 높이(verticalGap)와 하단 여백(verticalPadding)을 더해 총 필요한 높이를 구합니다.
            float neededHeight = Mathf.Abs(postionToInstantiate.y) + verticalGap + verticalPadding;

            // 실제 필요한 높이가 현재 RectTransform의 높이보다 클 경우에만 확장합니다.
            if (neededHeight > rectTransform.rect.height)
            {
                rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, neededHeight);
            }
        }

        public void ClearChips()
        {
            // Clear 시 다시 초기 상태 좌표로 되돌립니다.
            Initialize();
        }
    }
}