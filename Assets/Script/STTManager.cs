using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// WebSocket을 통해 실시간 STT 및 Feature 데이터를 전송하는 매니저
    /// </summary>
    public class STTManager : MonoBehaviour
    {
        [SerializeField] private string wsUrl = "ws://127.0.0.1:8000/ws/interview";

        public Action OnServerRequestEnd; // 서버에서 발화 종료를 감지했을 때 발생
        public Action<FinalResponse> OnTranscriptionReceived; // 최종 결과 수신
        public Action<byte[]> OnAudioChunkReceived; // 서버로부터 오디오 청크(Raw PCM) 수신 시 발생
        public Action OnAudioStreamEnded; // 서버에서 모든 오디오 스트리밍이 완료되었을 때 발생
        public Action<string> OnSubtitleReceived; // 서버로부터 자막 텍스트 수신 시 발생
        public Action<CorrectionRequestMessage> OnCorrectionRequested; // 저신뢰 교정 요청 수신 시 발생

        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;
        private bool _isFirstChunk = true;
        private bool _isConnecting = false;

        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1); // 웹소켓 연결에 따른 세마포어
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1); // 데이터 전송에 따른 세마포어

        private async void Start()
        {
            await ConnectAsync();
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _webSocket?.Dispose();
        }

        public async Task ConnectAsync()
        {
            // 이미 연결 중이거나 연결된 경우 빠른 탈출
            if (_webSocket?.State == WebSocketState.Open) return;
            if (_isConnecting) return;

            await _connectLock.WaitAsync();
            try
            {
                if (_webSocket?.State == WebSocketState.Open) return;
                _isConnecting = true;

                _cts?.Cancel();
                _webSocket?.Dispose();

                _webSocket = new ClientWebSocket();
                _cts = new CancellationTokenSource();

                // 🌟 오디오 스트리밍 최적화: 버퍼 크기를 8KB로 조절하여 레이턴시 단축 및 네이글 알고리즘 완화
                _webSocket.Options.SetBuffer(8192, 8192);
                _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

                await _webSocket.ConnectAsync(new Uri(wsUrl), _cts.Token);
                Debug.Log("[STT] WebSocket Connected!");
                _ = ReceiveLoop();
            }
            catch (Exception e)
            {
                Debug.LogError($"[STT] Connection Error: {e.Message}");
                _ = ScheduleReconnectAsync();
            }
            finally
            {
                _isConnecting = false;
                _connectLock.Release();
            }
        }

        private async Task ScheduleReconnectAsync()
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            
            // 3초 대기 후 자동 재연결 시도
            await Task.Delay(3000, _cts.Token);
            if (_webSocket?.State != WebSocketState.Open)
            {
                Debug.Log("[STT] Attempting to reconnect to STT Worker...");
                await ConnectAsync();
            }
        }

        /// <summary>
        /// 발화가 새로 시작됨을 알림 (헤더 전송 준비)
        /// </summary>
        public void ResetUtteranceState()
        {
            _isFirstChunk = true;
        }

        /// <summary>
        /// 오디오 청크를 바이너리로 전송 (첫 청크만 헤더 포함)
        /// </summary>
        public async Task SendAudioChunkAsync(AudioClip clip)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                await ConnectAsync();
            if (_webSocket?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                byte[] audioBytes;
                if (_isFirstChunk)
                {
                    audioBytes = AudioUtils.GetWavBytes(clip);
                    _isFirstChunk = false;
                }
                else
                {
                    audioBytes = AudioUtils.GetRawPcmBytes(clip);
                }

                await _webSocket.SendAsync(
                    new ArraySegment<byte>(audioBytes),
                    WebSocketMessageType.Binary, true, _cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[STT] Failed to send audio chunk: {e.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 발화 종료 신호와 Feature 데이터를 전송
        /// </summary>
        public async Task SendEndUtteranceAsync(FeatureData features)
        {
            if (_webSocket?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                string json = $"{{\"type\":\"utterance_end\",\"features\":{{" +
                            $"\"speakingTime\":{features.speakingTime:F2}," +
                            $"\"pauseCount\":{features.pauseCount}," +
                            $"\"averageVolume\":{features.averageVolume}}}}}";

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, _cts.Token);
                
                _isFirstChunk = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[STT] Failed to send end utterance: {e.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 재발화 교정 종료 신호와 메타데이터를 전송
        /// </summary>
        public async Task SendCorrectionEndUtteranceAsync(FeatureData features, int startIdx, int endIdx, string[] originalWords)
        {
            if (_webSocket?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                string wordsJson = "[]";
                if (originalWords != null && originalWords.Length > 0)
                {
                    StringBuilder sb = new StringBuilder("[");
                    for (int i = 0; i < originalWords.Length; i++)
                    {
                        sb.Append($"\"{originalWords[i].Replace("\"", "\\\"")}\"");
                        if (i < originalWords.Length - 1) sb.Append(",");
                    }
                    sb.Append("]");
                    wordsJson = sb.ToString();
                }

                string json = $"{{\"type\":\"utterance_end\"," +
                              $"\"mode\":\"correction\"," +
                              $"\"target_range\":[{startIdx},{endIdx}]," +
                              $"\"original_words\":{wordsJson}," +
                              $"\"features\":{{" +
                              $"\"speakingTime\":{features.speakingTime:F2}," +
                              $"\"pauseCount\":{features.pauseCount}," +
                              $"\"averageVolume\":{features.averageVolume}}}}}";

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, _cts.Token);
                
                _isFirstChunk = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[STT] Failed to send correction end utterance: {e.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 수정 없이 그대로 질문을 전송
        /// </summary>
        public async Task SendAnywayAsync(string text, FeatureData features)
        {
            if (_webSocket?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                string json = $"{{\"type\":\"send_anyway\"," +
                              $"\"text\":\"{text.Replace("\"", "\\\"")}\"," +
                              $"\"features\":{{" +
                              $"\"speakingTime\":{features.speakingTime:F2}," +
                              $"\"pauseCount\":{features.pauseCount}," +
                              $"\"averageVolume\":{features.averageVolume}}}}}";

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, _cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[STT] Failed to send send_anyway command: {e.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 교정을 포기하고 전체 발화를 폐기
        /// </summary>
        public async Task SendDiscardAsync()
        {
            if (_webSocket?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                string json = "{\"type\":\"discard\"}";
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, _cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[STT] Failed to send discard command: {e.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoop()
        {
            byte[] buffer = new byte[1024 * 32];
            using (var ms = new System.IO.MemoryStream())
            {
                while (_webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = null;
                    try
                    {
                        ms.SetLength(0); // 매 메시지마다 스트림 초기화
                        do
                        {
                            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, _cts.Token);
                            _ = ScheduleReconnectAsync();
                            break;
                        }

                        if (ms.Length == 0) continue;

                        byte[] data = ms.ToArray();

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            // 바이너리 데이터는 TTS Worker에서 온 오디오 청크(Raw PCM)로 간주
                            OnAudioChunkReceived?.Invoke(data);
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string message = Encoding.UTF8.GetString(data);
                            Debug.Log($"[STT] Received JSON: {message}");

                            try
                            {
                                ServerMessage msg = JsonUtility.FromJson<ServerMessage>(message);
                                if (msg.type == "request_end")
                                {
                                    OnServerRequestEnd?.Invoke();
                                }
                                else if (msg.type == "final")
                                {
                                    FinalResponse response = JsonUtility.FromJson<FinalResponse>(message);
                                    OnTranscriptionReceived?.Invoke(response);
                                }
                                else if (msg.type == "correction_request")
                                {
                                    CorrectionRequestMessage corrMsg = JsonUtility.FromJson<CorrectionRequestMessage>(message);
                                    OnCorrectionRequested?.Invoke(corrMsg);
                                }
                                else if (msg.type == "subtitle")
                                {
                                    SubtitleMessage subMsg = JsonUtility.FromJson<SubtitleMessage>(message);
                                    OnSubtitleReceived?.Invoke(subMsg.text);
                                }
                                else if (msg.type == "tts_end")
                                {
                                    OnAudioStreamEnded?.Invoke();
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogWarning($"Failed to parse server message: {e.Message}");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[STT] Receive Loop Error: {e.Message}");
                        _ = ScheduleReconnectAsync();
                        break;
                    }
                }
            }
        }

        [Serializable]
        public class ServerMessage {
            public string type;
        }

        [Serializable]
        public class SubtitleMessage {
            public string type;
            public string text;
        }

        [Serializable]
        public class FinalResponse {
            public string type;
            public TranscriptionData data;
        }

        [Serializable]
        public class TranscriptionData {
            public string sttText;
            public float speakingTime;
            public int pauseCount;
            public float averageVolume;
        }

        [Serializable]
        public class CorrectionRequestMessage {
            public string type;
            public TranscriptionData data;
            public string[] words;
            public float[] word_confidences;
        }
    }
}
