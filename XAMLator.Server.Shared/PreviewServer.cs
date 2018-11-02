﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XAMLator.HttpServer;

namespace XAMLator.Server
{
	/// <summary>
	/// Preview server that process HTTP requests, evaluates them in the <see cref="VM"/>
	/// and preview them with the <see cref="Previewer"/>.
	/// </summary>
	public class PreviewServer
	{
		static readonly PreviewServer serverInstance = new PreviewServer();

		VM vm;
		TaskScheduler mainScheduler;
		IPreviewer previewer;
		bool isRunning;
		TcpCommunicatorClient client;

		internal static PreviewServer Instance => serverInstance;

		PreviewServer()
		{
			client = new TcpCommunicatorClient();
			client.DataReceived += HandleDataReceived;
		}

		public static Task<bool> Run(Dictionary<Type, object> viewModelsMapping = null, IPreviewer previewer = null)
		{
			return Instance.RunInternal(viewModelsMapping, previewer);
		}

		internal async Task<bool> RunInternal(Dictionary<Type, object> viewModelsMapping, IPreviewer previewer)
		{
			if (isRunning)
			{
				return true;
			}

			mainScheduler = TaskScheduler.FromCurrentSynchronizationContext();
			await RegisterDevice();
			if (viewModelsMapping == null)
			{
				viewModelsMapping = new Dictionary<Type, object>();
			}
			if (previewer == null)
			{
				previewer = new Previewer(viewModelsMapping);
			}
			this.previewer = previewer;
			vm = new VM();
			isRunning = true;
			return true;
		}

		async Task RegisterDevice()
		{
			var ideIP = GetIdeIPFromResource();
			try
			{
				Log.Information($"Connecting to IDE at tcp://{ideIP}:{Constants.DEFAULT_PORT}");
				await client.Connect(ideIP, Constants.DEFAULT_PORT);
			}
			catch (Exception ex)
			{
				Log.Error($"Couldn't register device at {ideIP}");
				Log.Exception(ex);
			}
		}

		string GetIdeIPFromResource()
		{
			try
			{
				using (Stream stream = GetType().Assembly.GetManifestResourceStream(Constants.IDE_IP_RESOURCE_NAME))
				using (StreamReader reader = new StreamReader(stream))
				{
					return reader.ReadToEnd().Split('\n')[0].Trim();
				}
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
				return null;
			}
		}

		async void HandleDataReceived(object sender, object e)
		{
			await HandleEvalRequest((e as JContainer).ToObject<EvalRequest>());
		}

		async Task HandleEvalRequest(EvalRequest request)
		{
			EvalResponse evalResponse = new EvalResponse();
			EvalResult result;
			try
			{
				result = await vm.Eval(request, mainScheduler, CancellationToken.None);
				Log.Information($"Visualizing result {result.Result}");
				if (result.HasResult)
				{
					var tcs = new TaskCompletionSource<bool>();
					Xamarin.Forms.Device.BeginInvokeOnMainThread(async () =>
					{
						try
						{
							await previewer.Preview(result);
							tcs.SetResult(true);
						}
						catch (Exception ex)
						{
							await previewer.NotifyError(new ErrorViewModel("Oh no! An exception!", ex));
							tcs.SetException(ex);
						}
					});
					await tcs.Task;
				}
				else
				{
					Xamarin.Forms.Device.BeginInvokeOnMainThread(async () =>
					{
						await previewer.NotifyError(new ErrorViewModel("Oh no! An evaluation error!", result));
					});
				}
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
			}
		}
	}
}
