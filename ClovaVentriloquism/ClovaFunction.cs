﻿using CEK.CSharp;
using CEK.CSharp.Models;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System;
using DurableTask.Core;

namespace ClovaVentriloquism
{
    public static class ClovaFunction
    {
        /// <summary>
        /// CEKのエンドポイント。
        /// </summary>
        /// <param name="req"></param>
        /// <param name="client"></param>
        /// <param name="context"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        [FunctionName(nameof(CEKEndpoint))]
        public static async Task<IActionResult> CEKEndpoint(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequest req,
            [OrchestrationClient] DurableOrchestrationClient client,
            ExecutionContext context,
            ILogger log)
        {
            var cekResponse = new CEKResponse();
            var clovaClient = new ClovaClient();
            var cekRequest = await clovaClient.GetRequest(req.Headers["SignatureCEK"], req.Body);
            var userId = cekRequest.Session.User.UserId;

            log.LogInformation(cekRequest.Request.Type.ToString());

            switch (cekRequest.Request.Type)
            {
                case RequestType.LaunchRequest:
                    {
                        await client.StartNewAsync(nameof(WaitForLineInput), userId, null);
                        cekResponse.AddText("LINEに入力をした内容をしゃべります。");

                        // 無音無限ループに入る
                        KeepClovaWaiting(cekResponse);
                        break;
                    }
                case RequestType.IntentRequest:
                    {
                        // インテントリクエストは特に用意しない
                        break;
                    }
                case RequestType.EventRequest:
                    {
                        // オーディオイベントの制御
                        // Clovaでのオーディオ再生が終わった際に呼び出される
                        if (cekRequest.Request.Event.Namespace == "AudioPlayer")
                        {
                            if (cekRequest.Request.Event.Name == "PlayFinished")
                            {
                                // 終わっていなければ無音再生リクエストを繰り返す
                                var status = await client.GetStatusAsync(userId);
                                if (status.RuntimeStatus == OrchestrationRuntimeStatus.ContinuedAsNew ||
                                    status.RuntimeStatus == OrchestrationRuntimeStatus.Pending ||
                                    status.RuntimeStatus == OrchestrationRuntimeStatus.Running)
                                {
                                    KeepClovaWaiting(cekResponse);
                                }
                                else if (status.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
                                {
                                    // 完了していた場合（＝LINEからの外部イベント処理が実行された場合）
                                    // 再度セッション継続
                                    KeepClovaWaiting(cekResponse);
                                    // 入力内容を話させる
                                    cekResponse.AddText(status.Output.ToObject<string>());
                                    // オーケストレーターを再実行
                                    await client.StartNewAsync(nameof(WaitForLineInput), userId, null);
                                }
                                else if (status.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
                                {
                                    // 失敗していたら結果をしゃべって終了
                                    cekResponse.AddText("失敗しました。");
                                }
                                else if (status.RuntimeStatus == OrchestrationRuntimeStatus.Terminated)
                                {
                                    // Botからのスキル停止指示
                                    cekResponse.AddText("腹話術を終了します。");
                                }
                            }
                            else if (cekRequest.Request.Event.Name == "PlayPaused")
                            {
                                await client.TerminateAsync(userId, "PlayPaused");
                            }
                        }
                        break;
                    }
                case RequestType.SessionEndedRequest:
                    {
                        // スキル終了の場合は処理もキャンセル
                        await client.TerminateAsync(userId, "SessionEndedRequest");
                        cekResponse.AddText("終了します。");
                        break;
                    }
            }

            return new OkObjectResult(cekResponse);
        }

        /// <summary>
        /// Clovaを無音再生で待機させます。
        /// </summary>
        /// <param name="cekResponse"></param>
        private static void KeepClovaWaiting(CEKResponse cekResponse)
        {
            // 無音mp3の再生指示
            cekResponse.Response.Directives.Add(new Directive()
            {
                Header = new DirectiveHeader()
                {
                    Namespace = DirectiveHeaderNamespace.AudioPlayer,
                    Name = DirectiveHeaderName.Play
                },
                Payload = new AudioPlayPayload
                {
                    AudioItem = new AudioItem
                    {
                        AudioItemId = "silent-audio",
                        TitleText = "Durable Session",
                        TitleSubText1 = "Azure Functions",
                        TitleSubText2 = "Durable Functions",
                        Stream = new AudioStreamInfoObject
                        {
                            BeginAtInMilliseconds = 0,
                            ProgressReport = new ProgressReport
                            {
                                ProgressReportDelayInMilliseconds = null,
                                ProgressReportIntervalInMilliseconds = null,
                                ProgressReportPositionInMilliseconds = null
                            },
                            Url = Consts.SilentAudioFileUri,
                            UrlPlayable = true
                        }
                    },
                    PlayBehavior = AudioPlayBehavior.REPLACE_ALL,
                    Source = new Source
                    {
                        Name = "Microsoft Azure"
                    }
                }
            });
            //cekResponse.ShouldEndSession = false;
        }

        /// <summary>
        /// LINEからのイベントを待機し、その入力内容を返すオーケストレーター。
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        [FunctionName(nameof(WaitForLineInput))]
        public static async Task<string> WaitForLineInput(
            [OrchestrationTrigger] DurableOrchestrationContext context,
            ILogger log)
        {
            try
            {
                return await context.WaitForExternalEvent<string>(Consts.DurableEventNameLineVentriloquismInput);
            }
            catch (Exception ex)
            {
                log.LogError(ex.StackTrace);
                throw;
            }
        }

        /// <summary>
        /// 実行履歴を削除するタイマー関数。1日1回、午前12時に実行されます。
        /// </summary>
        /// <param name="client"></param>
        /// <param name="myTimer"></param>
        /// <returns></returns>
        [FunctionName(nameof(HistoryCleanerFunction))]
        public static Task HistoryCleanerFunction(
            [OrchestrationClient] DurableOrchestrationClient client,
            [TimerTrigger("0 0 12 * * *")]TimerInfo myTimer)
        {
            return client.PurgeInstanceHistoryAsync(
                DateTime.MinValue,
                DateTime.UtcNow.AddDays(-1),
                new List<OrchestrationStatus>
                {
                    OrchestrationStatus.Completed
                });
        }
    }
}