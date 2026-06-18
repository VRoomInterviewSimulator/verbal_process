using System;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// VAD, STT, Feature 추출의 전체 파이프라인 흐름을 제어하는 컨트롤러 클래스
    /// </summary>
    public class PipelineController : MonoBehaviour
    {
        [SerializeField] private VoiceActivityDetector vad;
        [SerializeField] private STTManager sttManager;
        [SerializeField] private Speaker speaker;
        [SerializeField] private TMPro.TMP_Text subtitleText; // UI 자막 텍스트 컴포넌트 (선택 사항)
        [SerializeField] private SubtitleCorrectionPanel correctionPanel; // 자막 교정 UI 패널

        // 교정 관련 상태 변수들
        private bool _isCorrectionMode = false;
        private int _correctionStartIdx = -1;
        private int _correctionEndIdx = -1;
        private string[] _originalWords;
        private FeatureData _originalFeatures;
        private string _originalTextForCorrection = "";

        private void OnEnable()
        {
            if (vad != null)
            {
                vad.OnUtteranceEnded += HandleUtteranceEnded;
                vad.OnAudioChunkCaptured += HandleOnAudioChunkCaptured;
                vad.OnSpeakingStarted += HandleSpeakingStarted;
            }
            else
            {
                Debug.LogWarning("PipelineController: VoiceActivityDetector is not assigned!");
            }

            if (sttManager != null)
            {
                sttManager.OnServerRequestEnd += HandleServerRequestEnd;
                sttManager.OnTranscriptionReceived += HandleTranscriptionReceived;
                sttManager.OnAudioStreamEnded += HandleAudioStreamEnded;
                sttManager.OnCorrectionRequested += HandleOnCorrectionRequested;
                
                if (speaker != null)
                {
                    sttManager.OnAudioChunkReceived += speaker.HandleAudioChunkReceived;
                    sttManager.OnSubtitleReceived += speaker.HandleSubtitleReceived;
                    speaker.OnPlaybackFinished += HandlePlaybackFinished;
                    speaker.OnSubtitleTextChanged += HandleSubtitleTextChanged;
                }
            }
        }

        private void OnDisable()
        {
            if (vad != null)
            {
                vad.OnUtteranceEnded -= HandleUtteranceEnded;
                vad.OnAudioChunkCaptured -= HandleOnAudioChunkCaptured;
                vad.OnSpeakingStarted -= HandleSpeakingStarted;
            }

            if (sttManager != null)
            {
                sttManager.OnServerRequestEnd -= HandleServerRequestEnd;
                sttManager.OnTranscriptionReceived -= HandleTranscriptionReceived;
                sttManager.OnAudioStreamEnded -= HandleAudioStreamEnded;
                sttManager.OnCorrectionRequested -= HandleOnCorrectionRequested;

                if (speaker != null)
                {
                    sttManager.OnAudioChunkReceived -= speaker.HandleAudioChunkReceived;
                    sttManager.OnSubtitleReceived -= speaker.HandleSubtitleReceived;
                    speaker.OnPlaybackFinished -= HandlePlaybackFinished;
                    speaker.OnSubtitleTextChanged -= HandleSubtitleTextChanged;
                }
            }
        }

        private void HandleSpeakingStarted()
        {
            if (sttManager != null)
            {
                // 새 발화가 시작될 때 STT 매니저의 상태(헤더 전송 여부)를 초기화
                sttManager.ResetUtteranceState();
            }

            if (speaker != null)
            {
                // 사용자가 다시 말을 시작하면 기존 출력 중이던 오디오 중단 (Barge-in)
                speaker.StopAndClear();
            }
        }

        private void HandleServerRequestEnd()
        {
            if (vad != null)
            {
                vad.ForceEnd();
            }
        }

        private void HandleTranscriptionReceived(STTManager.FinalResponse response)
        {
            Debug.Log($"<color=cyan>[Pipeline] Final STT Result: {response.data.sttText}</color>");
            Debug.Log($"[Pipeline] Stats - Time: {response.data.speakingTime:F2}s, Pauses: {response.data.pauseCount}, Vol: {response.data.averageVolume:F4}");
            
            // STT 결과는 UI 업데이트 등에 활용하며, VAD 재활성화는 오디오 재생 종료 시(HandlePlaybackFinished) 수행합니다.
        }

        private void HandleAudioStreamEnded()
        {
            if (speaker != null)
            {
                // 서버에서 더 이상 오디오가 오지 않음을 Speaker에 알림
                speaker.SetEndOfStream();
            }
        }

        private void HandlePlaybackFinished()
        {
            // AI의 모든 답변 재생이 끝났을 때 VAD를 다시 활성화하여 다음 입력을 대기합니다.
            if (vad != null)
            {
                vad.enabled = true;
                Debug.Log("[Pipeline] Speaker finished. VAD re-enabled.");
            }
        }

        private void HandleSubtitleTextChanged(string text)
        {
            if (subtitleText != null)
            {
                subtitleText.text = text;
            }
            if (!string.IsNullOrEmpty(text))
            {
                //Debug.Log($"[Subtitle Display] {text}");
            }
        }

        private async void HandleOnAudioChunkCaptured(AudioClip Clip)
        {
            if (sttManager == null) return;
            try
            {
                // WebSocket을 통해 실시간 오디오 데이터 전송
                await sttManager.SendAudioChunkAsync(Clip);
            }
            catch (Exception e)
            {
                Debug.LogError($"Pipeline Error (Chunk): {e.Message}");
            }
        }

        private async void HandleUtteranceEnded(VoiceActivityDetector.VoiceFeatures features)
        {
            if (sttManager == null) return;

            try
            {
                if (_isCorrectionMode)
                {
                    Debug.Log("[Pipeline] Re-speak completed. Sending Correction Feature via WebSocket...");
                    FeatureData currentFeatures = new FeatureData(features);
                    await sttManager.SendCorrectionEndUtteranceAsync(currentFeatures, _correctionStartIdx, _correctionEndIdx, _originalWords);
                    
                    if (correctionPanel != null) correctionPanel.Close();
                    ResetCorrectionState();
                }
                else
                {
                    Debug.Log("Pipeline: Utterance ended. Sending Feature via WebSocket...");
                    FeatureData featureData = new FeatureData(features);
                    await sttManager.SendEndUtteranceAsync(featureData);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Pipeline Error (End): {e.Message}");
            }
        }

        private void HandleOnCorrectionRequested(STTManager.CorrectionRequestMessage msg)
        {
            Debug.Log("[Pipeline] Low confidence STT detected. Opening correction panel.");

            // VAD 비활성화 (교정 UI가 떴을 때 말을 하더라도 자동으로 음성이 전송되는 것을 차단)
            if (vad != null) vad.enabled = false;

            _originalTextForCorrection = msg.data != null ? msg.data.sttText : "";
            if (msg.data != null)
            {
                _originalFeatures = new FeatureData(new VoiceActivityDetector.VoiceFeatures
                {
                    speakingTime = msg.data.speakingTime,
                    silenceCount = msg.data.pauseCount,
                    averageVolume = msg.data.averageVolume
                });
            }

            if (correctionPanel != null)
            {
                correctionPanel.Open(
                    msg,
                    HandleSendAnyway,
                    HandleDiscardCorrection,
                    HandleReSpeakStart
                );
            }
        }

        private async void HandleSendAnyway()
        {
            if (sttManager != null && _originalFeatures != null)
            {
                await sttManager.SendAnywayAsync(_originalTextForCorrection, _originalFeatures);
            }
            ResetCorrectionState();
        }

        private async void HandleDiscardCorrection()
        {
            if (sttManager != null)
            {
                await sttManager.SendDiscardAsync();
            }
            
            // 즉시 일반 VAD 모드로 복귀
            if (vad != null) vad.enabled = true;
            
            ResetCorrectionState();
        }

        private void HandleReSpeakStart(int startIdx, int endIdx, string[] words)
        {
            _isCorrectionMode = true;
            _correctionStartIdx = startIdx;
            _correctionEndIdx = endIdx;
            _originalWords = words;

            // 재발화를 녹음할 수 있도록 VAD 일시 재활성화
            if (vad != null)
            {
                vad.enabled = true;
                sttManager.ResetUtteranceState();
            }
        }

        private void ResetCorrectionState()
        {
            _isCorrectionMode = false;
            _correctionStartIdx = -1;
            _correctionEndIdx = -1;
            _originalWords = null;
            _originalFeatures = null;
            _originalTextForCorrection = "";
            if (correctionPanel != null)
            {
                correctionPanel.ResetButtonInteractions();
            }
        }
    }
}
