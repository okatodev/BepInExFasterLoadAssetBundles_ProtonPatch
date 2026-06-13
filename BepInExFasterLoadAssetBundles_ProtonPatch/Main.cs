using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using BepInExFasterLoadAssetBundles.Helpers;
using BepInExFasterLoadAssetBundles.Managers;
using BepInExFasterLoadAssetBundles.Models;
using HarmonyLib;
using Mono.Cecil;
using Newtonsoft.Json;
using UnityEngine;

namespace BepInExFasterLoadAssetBundles
{
	public class BepInExFasterLoadAssetBundlesPatcher
	{
		internal static Harmony Harmony { get; } = new Harmony("BepInExFasterLoadAssetBundlesPatcher");

		public static IEnumerable<string> TargetDLLs { get; } = Array.Empty<string>();

		public static void Finish()
		{
			Harmony.PatchAll(typeof(BepInExFasterLoadAssetBundlesPatcher).Assembly);
		}

		public static void Patch(AssemblyDefinition _)
		{
		}
	}

	[HarmonyPatch]
	internal static class Patcher
	{
		internal static ManualLogSource Logger { get; private set; }

		internal static AssetBundleManager AssetBundleManager { get; private set; }

		internal static MetadataManager MetadataManager { get; private set; }

		[HarmonyPatch(typeof(Chainloader), "Initialize")]
		[HarmonyPostfix]
		public static void ChainloaderInitialized()
		{
			AsyncHelper.InitUnitySynchronizationContext();
			Logger = BepInEx.Logging.Logger.CreateLogSource("BepInExFasterLoadAssetBundlesPatcher");
			string fullName = new DirectoryInfo(Application.dataPath).Parent.FullName;
			string cachePath = Path.Combine(fullName, "Cache", "AssetBundles");
			if (!Directory.Exists(cachePath))
			{
				Directory.CreateDirectory(cachePath);
			}
			AssetBundleManager = new AssetBundleManager(cachePath);
			MetadataManager = new MetadataManager(Path.Combine(cachePath, "metadata.json"));
			Patch();
		}

		private static void Patch()
		{
			Type typeFromHandle = typeof(Patcher);
			Harmony harmony = BepInExFasterLoadAssetBundlesPatcher.Harmony;
			BindingFlags all = AccessTools.all;
			HarmonyMethod prefix = new HarmonyMethod(typeFromHandle.GetMethod("LoadAssetBundleFromFileFast", all));
			Type typeFromHandle2 = typeof(AssetBundle);
			string[] methods = { "LoadFromFile", "LoadFromFileAsync" };
			
			foreach (string method in methods)
			{
				harmony.Patch(AccessTools.Method(typeFromHandle2, method, new[] { typeof(string) }), prefix);
				harmony.Patch(AccessTools.Method(typeFromHandle2, method, new[] { typeof(string), typeof(uint) }), prefix);
				harmony.Patch(AccessTools.Method(typeFromHandle2, method, new[] { typeof(string), typeof(uint), typeof(ulong) }), prefix);
			}
			
			harmony.Patch(AccessTools.Method(typeFromHandle2, "LoadFromStreamInternal"), new HarmonyMethod(typeFromHandle.GetMethod("LoadAssetBundleFromStreamFast", all)));
			harmony.Patch(AccessTools.Method(typeFromHandle2, "LoadFromStreamAsyncInternal"), new HarmonyMethod(typeFromHandle.GetMethod("LoadAssetBundleFromStreamAsyncFast", all)));
			harmony.Patch(AccessTools.Method(typeFromHandle2, "LoadFromMemory_Internal"), new HarmonyMethod(typeFromHandle.GetMethod("LoadAssetBundleFromMemoryFast", all)));
		}

		private static void LoadAssetBundleFromFileFast(ref string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return;
			}
			try
			{
				using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 16777216, FileOptions.SequentialScan);
				if (HandleStreamBundle(stream, out string path2))
				{
					path = path2;
				}
			}
			catch (Exception arg)
			{
				Logger.LogError($"Failed to decompress assetbundle\n{arg}");
			}
		}

		private static bool LoadAssetBundleFromStreamFast(Stream stream, ref AssetBundle __result)
		{
			if (HandleStreamBundle(stream, out string path))
			{
				__result = AssetBundle.LoadFromFile(path, 0u, 0uL);
				return false;
			}
			return true;
		}

		private static bool LoadAssetBundleFromStreamAsyncFast(Stream stream, ref AssetBundleCreateRequest __result)
		{
			if (HandleStreamBundle(stream, out string path))
			{
				__result = AssetBundle.LoadFromFileAsync(path, 0u, 0uL);
				return false;
			}
			return true;
		}

		private static bool LoadAssetBundleFromMemoryFast(byte[] binary, ref AssetBundle __result)
		{
			byte[] array = ArrayPool<byte>.Shared.Rent(binary.Length);
			binary.CopyTo(array, 0);
			using MemoryStream stream = new MemoryStream(array, 0, binary.Length);
			string path;
			bool flag = HandleStreamBundle(stream, out path);
			ArrayPool<byte>.Shared.Return(array);
			if (flag)
			{
				__result = AssetBundle.LoadFromFile(path, 0u, 0uL);
				return false;
			}
			return true;
		}

		private static bool HandleStreamBundle(Stream stream, out string path)
		{
			long position = stream.Position;
			try
			{
				return AssetBundleManager.TryRecompressAssetBundle(stream, out path);
			}
			catch (Exception arg)
			{
				Logger.LogError($"Failed to decompress assetbundle\n{arg}");
			}
			stream.Position = position;
			path = null;
			return false;
		}
	}
}

namespace BepInExFasterLoadAssetBundles.Models
{
	internal class Metadata
	{
		public string UncompressedAssetBundleName { get; set; }

		public string OriginalAssetBundleHash { get; set; }

		public bool ShouldNotDecompress { get; set; }

		public DateTime LastAccessTime { get; set; }
	}
}

namespace BepInExFasterLoadAssetBundles.Managers
{
	internal class AssetBundleManager
	{
		private readonly struct WorkAsset
		{
			public string Path { get; }

			public string Hash { get; }

			public bool DeleteBundleAfterOperation { get; }

			public WorkAsset(string path, string hash, bool deleteBundleAfterOperation)
			{
				Path = path;
				Hash = hash;
				DeleteBundleAfterOperation = deleteBundleAfterOperation;
			}
		}

		private readonly ConcurrentQueue<WorkAsset> m_WorkAssets = new ConcurrentQueue<WorkAsset>();

		private readonly object m_Lock = new object();

		private readonly string m_PathForTemp;

		private bool m_IsProcessingQueue;

		public string CachePath { get; }

		public AssetBundleManager(string cachePath)
		{
			CachePath = cachePath;
			if (!Directory.Exists(CachePath))
			{
				Directory.CreateDirectory(CachePath);
			}
			m_PathForTemp = Path.Combine(CachePath, "temp");
			if (!Directory.Exists(m_PathForTemp))
			{
				Directory.CreateDirectory(m_PathForTemp);
			}
			DeleteTempFiles();
		}

		private void DeleteTempFiles()
		{
			int count2 = 0;
			try
			{
				foreach (string item in Directory.EnumerateFiles(CachePath, "*.tmp").Concat(Directory.EnumerateFiles(m_PathForTemp, "*.assetbundle")))
				{
					DeleteFileSafely(ref count2, item);
				}
			}
			catch (Exception arg)
			{
				Patcher.Logger.LogError($"Failed to delete temp files\n{arg}");
			}
			if (count2 > 0)
			{
				Patcher.Logger.LogWarning($"Deleted {count2} temp files");
			}
			
			static void DeleteFileSafely(ref int count, string tempFile)
			{
				if (!FileHelper.TryDeleteFile(tempFile, out Exception exception))
				{
					Patcher.Logger.LogError($"Failed to delete temp file\n{exception}");
				}
				else
				{
					count++;
				}
			}
		}

		public bool TryRecompressAssetBundle(Stream stream, out string path)
		{
			if (BundleHelper.CheckBundleIsAlreadyDecompressed(stream))
			{
				Patcher.Logger.LogInfo("Original bundle is already uncompressed, using it instead");
				path = null;
				return false;
			}
			Span<char> span = stackalloc char[32];
			HashingHelper.WriteHash(span, stream);
			path = null;
			if (FindCachedBundleByHash(span, out string path2))
			{
				if (path2 != null)
				{
					path = path2;
					return true;
				}
				Patcher.Logger.LogDebug("Found assetbundle metadata, but path was null. Probably bundle is already uncompressed!");
				return false;
			}
			if (stream is FileStream fileStream)
			{
				path = string.Copy(fileStream.Name);
				RecompressAssetBundleInternal(new WorkAsset(path, span.ToString(), deleteBundleAfterOperation: false));
				return false;
			}
			string path3 = Guid.NewGuid().ToString("N") + ".assetbundle";
			string path4 = Path.Combine(m_PathForTemp, path3);
			using (FileStream fileStream2 = new FileStream(path4, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.SequentialScan))
			{
				stream.Seek(0L, SeekOrigin.Begin);
				stream.CopyTo(fileStream2);
			}
			RecompressAssetBundleInternal(new WorkAsset(path4, span.ToString(), deleteBundleAfterOperation: true));
			return false;
		}

		public void DeleteCachedAssetBundle(string path)
		{
			FileHelper.TryDeleteFile(path, out Exception exception);
			if (exception != null)
			{
				Patcher.Logger.LogError($"Failed to delete uncompressed assetbundle\n{exception}");
			}
		}

		private bool FindCachedBundleByHash(ReadOnlySpan<char> hash, out string path)
		{
			path = null;
			Metadata metadata2 = Patcher.MetadataManager.FindMetadataByHash(hash);
			if (metadata2 == null)
			{
				return false;
			}
			if (metadata2.ShouldNotDecompress)
			{
				ModifyAccessTimeAndSave(metadata2);
				return true;
			}
			if (metadata2.UncompressedAssetBundleName == null)
			{
				return false;
			}
			string text = Path.Combine(CachePath, metadata2.UncompressedAssetBundleName);
			if (!File.Exists(text))
			{
				Patcher.Logger.LogWarning($"Failed to find decompressed assetbundle at \"{text}\". Probably it was deleted?");
				Patcher.MetadataManager.DeleteMetadata(metadata2);
				return false;
			}
			Patcher.Logger.LogDebug($"Loading uncompressed bundle \"{metadata2.UncompressedAssetBundleName}\"");
			path = text;
			ModifyAccessTimeAndSave(metadata2);
			return true;
			
			static void ModifyAccessTimeAndSave(Metadata metadata)
			{
				metadata.LastAccessTime = DateTime.Now;
				Patcher.MetadataManager.SaveMetadata(metadata);
			}
		}

		private void RecompressAssetBundleInternal(WorkAsset workAsset)
		{
			if (!DriveHelper.HasDriveSpaceOnPath(CachePath, 10L))
			{
				Patcher.Logger.LogWarning("Ignoring request of decompressing, because the free drive space is less than 10GB");
				return;
			}
			Patcher.Logger.LogDebug($"Queued recompress of \"{Path.GetFileName(workAsset.Path)}\" assetbundle");
			m_WorkAssets.Enqueue(workAsset);
			StartRunner();
		}

		private void StartRunner()
		{
			if (m_IsProcessingQueue)
			{
				return;
			}
			lock (m_Lock)
			{
				if (m_IsProcessingQueue)
				{
					return;
				}
				m_IsProcessingQueue = true;
			}
			AsyncHelper.Schedule(ProcessQueue);
		}

		private async Task ProcessQueue()
		{
			try
			{
				WorkAsset result;
				while (m_WorkAssets.TryDequeue(out result))
				{
					await DecompressAssetBundleAsync(result);
				}
			}
			finally
			{
				lock (m_Lock)
				{
					if (m_IsProcessingQueue)
					{
						m_IsProcessingQueue = false;
					}
				}
			}
		}

		private async Task DecompressAssetBundleAsync(WorkAsset workAsset)
		{
			Metadata metadata = new Metadata
			{
				OriginalAssetBundleHash = workAsset.Hash,
				LastAccessTime = DateTime.Now
			};
			string originalFileName = Path.GetFileNameWithoutExtension(workAsset.Path);
			string outputName = originalFileName + "_" + metadata.GetHashCode() + ".assetbundle";
			string outputPath = Path.Combine(CachePath, outputName);
			BuildCompression buildCompression = BuildCompression.LZ4Runtime;
			Patcher.Logger.LogDebug($"Decompressing \"{originalFileName}\"");
			await FileHelper.RetryUntilFileIsClosedAsync(workAsset.Path);
			await AsyncHelper.SwitchToMainThread();
			
			AssetBundleRecompressOperation op = AssetBundle.RecompressAssetBundleAsync(workAsset.Path, outputPath, buildCompression, 0u, UnityEngine.ThreadPriority.Low);
			await op.WaitCompletionAsync<AssetBundleRecompressOperation>();
			
			AssetBundleLoadResult result = op.result;
			string humanReadableResult = op.humanReadableResult;
			bool success = op.success;
			string newHash = GetHashOfFile(outputPath);
			await AsyncHelper.SwitchToThreadPool();
			if (workAsset.DeleteBundleAfterOperation)
			{
				FileHelper.TryDeleteFile(workAsset.Path, out Exception _);
			}
			Patcher.Logger.LogDebug($"Result of decompression \"{originalFileName}\": {result} ({success}), {humanReadableResult}");
			if ((int)result != 0 || !success)
			{
				Patcher.Logger.LogWarning($"Failed to decompress a assetbundle at \"{workAsset.Path}\"\nResult: {result}, {humanReadableResult}");
			}
			else if (newHash.Equals(workAsset.Hash, StringComparison.InvariantCultureIgnoreCase))
			{
				Patcher.Logger.LogDebug($"Assetbundle \"{originalFileName}\" is already uncompressed, adding to ignore list");
				metadata.ShouldNotDecompress = true;
				Patcher.MetadataManager.SaveMetadata(metadata);
				DeleteCachedAssetBundle(outputPath);
			}
			else
			{
				Patcher.Logger.LogDebug($"Assetbundle \"{originalFileName}\" is now uncompressed!");
				metadata.UncompressedAssetBundleName = outputName;
				Patcher.MetadataManager.SaveMetadata(metadata);
			}
			
			static string GetHashOfFile(string filePath)
			{
				Span<char> destination = stackalloc char[32];
				HashingHelper.HashFile(destination, filePath);
				return destination.ToString();
			}
		}
	}
	
	internal class MetadataManager
	{
		private readonly string m_MetadataFile;

		private readonly object m_Lock = new object();

		private List<Metadata> m_Metadata;

		public MetadataManager(string metadataFile)
		{
			m_MetadataFile = metadataFile;
			LoadFile();
		}

		public Metadata FindMetadataByHash(ReadOnlySpan<char> hash)
		{
			lock (m_Lock)
			{
				foreach (Metadata metadatum in m_Metadata)
				{
					if (hash.SequenceEqual(metadatum.OriginalAssetBundleHash))
					{
						return metadatum;
					}
				}
			}
			return null;
		}

		public void SaveMetadata(Metadata metadata)
		{
			Metadata metadata2 = metadata;
			lock (m_Lock)
			{
				int num = m_Metadata.FindIndex((Metadata m) => m.OriginalAssetBundleHash.Equals(metadata2.OriginalAssetBundleHash, StringComparison.InvariantCulture));
				if (num == -1)
				{
					m_Metadata.Add(metadata2);
				}
				else
				{
					m_Metadata[num] = metadata2;
				}
			}
			SaveFile();
		}

		public void DeleteMetadata(Metadata metadata)
		{
			Metadata metadata2 = metadata;
			bool flag = false;
			lock (m_Lock)
			{
				int num = m_Metadata.FindIndex((Metadata m) => m.OriginalAssetBundleHash.Equals(metadata2.OriginalAssetBundleHash, StringComparison.InvariantCulture));
				if (num >= 0)
				{
					flag = true;
					m_Metadata.RemoveAt(num);
				}
			}
			if (flag)
			{
				SaveFile();
			}
		}

		private void LoadFile()
		{
			if (!File.Exists(m_MetadataFile))
			{
				m_Metadata = new List<Metadata>();
				return;
			}
			try
			{
				m_Metadata = JsonConvert.DeserializeObject<List<Metadata>>(File.ReadAllText(m_MetadataFile)) ?? new List<Metadata>();
			}
			catch (Exception arg)
			{
				Patcher.Logger.LogError($"Failed to deserialize metadata.json file\n{arg}");
				m_Metadata = new List<Metadata>();
				return;
			}
			if (!UpgradeMetadata())
			{
				DeleteOldBundles();
			}
		}

		private bool UpgradeMetadata()
		{
			bool flag = false;
			foreach (Metadata metadatum in m_Metadata)
			{
				bool flag2 = metadatum.LastAccessTime == default(DateTime);
				if (flag2)
				{
					metadatum.LastAccessTime = DateTime.Now;
				}
				flag = flag || flag2;
			}
			if (flag)
			{
				SaveFile();
			}
			return flag;
		}

		private void SaveFile()
		{
			lock (m_Lock)
			{
				File.WriteAllText(m_MetadataFile, JsonConvert.SerializeObject(m_Metadata));
			}
		}

		private void DeleteOldBundles()
		{
			for (int num = m_Metadata.Count - 1; num >= 0; num--)
			{
				Metadata metadata = m_Metadata[num];
				if (!((DateTime.Now - metadata.LastAccessTime).TotalDays < 3.0))
				{
					m_Metadata.RemoveAt(num);
					if (metadata.UncompressedAssetBundleName != null)
					{
						Patcher.Logger.LogInfo("Deleting unused asset bundle cache " + metadata.UncompressedAssetBundleName);
						Patcher.AssetBundleManager.DeleteCachedAssetBundle(Path.Combine(Patcher.AssetBundleManager.CachePath, metadata.UncompressedAssetBundleName));
					}
				}
			}
			int counter2 = 0;
			string[] files = Directory.GetFiles(Patcher.AssetBundleManager.CachePath, "*.assetbundle", SearchOption.TopDirectoryOnly);
			foreach (string path2 in files)
			{
				string bundleName = Path.GetFileName(path2);
				Metadata metadata2 = m_Metadata.Find((Metadata m) => m.UncompressedAssetBundleName != null && m.UncompressedAssetBundleName.Equals(bundleName, StringComparison.InvariantCulture));
				if (metadata2 == null)
				{
					DeleteFileSafely(ref counter2, path2);
				}
			}
			if (counter2 > 0)
			{
				Patcher.Logger.LogWarning($"Deleted {counter2} unknown bundles. Metadata file got corrupted?");
			}
			
			static void DeleteFileSafely(ref int counter, string path)
			{
				if (!FileHelper.TryDeleteFile(path, out Exception exception))
				{
					Patcher.Logger.LogWarning($"Failed to delete cache\n{exception}");
				}
				else
				{
					counter++;
				}
			}
		}
	}
}

namespace BepInExFasterLoadAssetBundles.Helpers
{
	internal static class AsyncHelper
	{
		[StructLayout(LayoutKind.Sequential, Size = 1)]
		public readonly struct SwitchToMainThreadAwaiter : ICriticalNotifyCompletion, INotifyCompletion
		{
			private static readonly SendOrPostCallback s_OnPostAction = OnPost;

			public bool IsCompleted => Thread.CurrentThread.ManagedThreadId == s_MainThreadId;

			public SwitchToMainThreadAwaiter GetAwaiter()
			{
				return this;
			}

			public void GetResult()
			{
			}

			public void OnCompleted(Action continuation)
			{
				UnsafeOnCompleted(continuation);
			}

			public void UnsafeOnCompleted(Action continuation)
			{
				s_SynchronizationContext.Post(s_OnPostAction, continuation);
			}

			private static void OnPost(object state)
			{
				if (state is Action action)
				{
					action();
				}
			}
		}

		[StructLayout(LayoutKind.Sequential, Size = 1)]
		public readonly struct SwitchToThreadPoolAwaiter : ICriticalNotifyCompletion, INotifyCompletion
		{
			private static readonly WaitCallback s_OnPostAction = OnPost;

			public bool IsCompleted => false;

			public SwitchToThreadPoolAwaiter GetAwaiter()
			{
				return this;
			}

			public void GetResult()
			{
			}

			public void OnCompleted(Action continuation)
			{
				ThreadPool.QueueUserWorkItem(s_OnPostAction, continuation);
			}

			public void UnsafeOnCompleted(Action continuation)
			{
				ThreadPool.UnsafeQueueUserWorkItem(s_OnPostAction, continuation);
			}

			private static void OnPost(object state)
			{
				if (state is Action action)
				{
					action();
				}
			}
		}

		private static SynchronizationContext s_SynchronizationContext;

		private static int s_MainThreadId = -1;

		public static void InitUnitySynchronizationContext()
		{
			s_SynchronizationContext = SynchronizationContext.Current;
			s_MainThreadId = Thread.CurrentThread.ManagedThreadId;
		}

		public static void Schedule(Func<Task> func)
		{
			Func<Task> func2 = func;
			Task.Run(async delegate
			{
				try
				{
					await func2();
				}
				catch (Exception ex)
				{
					Patcher.Logger.LogError(ex);
				}
			});
		}

		public static SwitchToMainThreadAwaiter SwitchToMainThread()
		{
			return default(SwitchToMainThreadAwaiter);
		}

		public static SwitchToThreadPoolAwaiter SwitchToThreadPool()
		{
			return default(SwitchToThreadPoolAwaiter);
		}
	}

	internal static class AsyncOperationHelper
	{
		public struct AsyncOperationAwaiter : ICriticalNotifyCompletion, INotifyCompletion
		{
			private AsyncOperation m_AsyncOperation;

			private Action m_ContinuationAction;

			public readonly bool IsCompleted => m_AsyncOperation.isDone;

			public AsyncOperationAwaiter(AsyncOperation asyncOperation)
			{
				m_AsyncOperation = asyncOperation;
				m_ContinuationAction = null;
			}

			public readonly AsyncOperationAwaiter GetAwaiter()
			{
				return this;
			}

			public void GetResult()
			{
				if (m_AsyncOperation != null)
				{
					m_AsyncOperation.completed -= OnCompleted;
				}
				m_AsyncOperation = null;
				m_ContinuationAction = null;
			}

			public void OnCompleted(Action continuation)
			{
				UnsafeOnCompleted(continuation);
			}

			public void UnsafeOnCompleted(Action continuation)
			{
				m_ContinuationAction = continuation;
				m_AsyncOperation.completed += OnCompleted;
			}

			private readonly void OnCompleted(AsyncOperation _)
			{
				m_ContinuationAction?.Invoke();
			}
		}

		public static AsyncOperationAwaiter WaitCompletionAsync<T>(this T op) where T : AsyncOperation
		{
			return new AsyncOperationAwaiter(op);
		}
	}

	internal static class BundleHelper
	{
		public static bool CheckBundleIsAlreadyDecompressed(Stream stream)
		{
			stream.Seek(0L, SeekOrigin.Begin);
			SkipString(stream);
			stream.Position += 4L;
			SkipString(stream);
			SkipString(stream);
			stream.Position += 16L;
			Span<byte> span = stackalloc byte[4];
			stream.Read(span);
			int num = BinaryPrimitives.ReadInt32BigEndian(span);
			int num2 = num & 0x3F;
			if (num2 == 0 || num2 == 2)
			{
				return true;
			}
			return false;
		}

		private static void SkipString(Stream stream)
		{
			while (stream.ReadByte() != 0)
			{
			}
		}
	}

	internal static class DriveHelper
	{
		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetDiskFreeSpaceEx(
			string lpDirectoryName,
			out ulong lpFreeBytesAvailable,
			out ulong lpTotalNumberOfBytes,
			out ulong lpTotalNumberOfFreeBytes);

		public static bool HasDriveSpaceOnPath(string path, long expectedSpaceGB)
		{
			try
			{
				if (GetDiskFreeSpaceEx(path, out ulong freeBytes, out _, out _))
				{
					return freeBytes > (ulong)(expectedSpaceGB * 1073741824L);
				}
			}
			catch (Exception)
			{
			}

			try
			{
				string pathRoot = Path.GetPathRoot(Path.GetFullPath(path));
				if (string.IsNullOrEmpty(pathRoot))
				{
					return true;
				}

				DriveInfo driveInfo = new DriveInfo(pathRoot);
				if (driveInfo.DriveType == DriveType.Unknown || driveInfo.TotalFreeSpace <= 0)
				{
					return true;
				}

				return driveInfo.TotalFreeSpace > expectedSpaceGB * 1073741824L;
			}
			catch (Exception)
			{
				return true;
			}
		}
	}

	internal static class FileHelper
	{
		public const long c_GBToBytes = 1073741824L;

		public const long c_MBToBytes = 1048576L;

		public static bool TryDeleteFile(string path, out Exception exception)
		{
			try
			{
				File.Delete(path);
				exception = null;
				return true;
			}
			catch (Exception ex)
			{
				exception = ex;
				return false;
			}
		}

		public static async Task RetryUntilFileIsClosedAsync(string path, int maxTries = 5)
		{
			int tries = maxTries;
			while (true)
			{
				int num = tries - 1;
				tries = num;
				if (num <= 0)
				{
					break;
				}
				try
				{
					using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
					{
					}
				}
				catch (IOException)
				{
					await Task.Delay(1000);
				}
			}
		}
	}

	internal class HashingHelper
	{
		public static int HashFile(Span<char> destination, string path)
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 16777216, FileOptions.SequentialScan);
			return WriteHash(destination, stream);
		}

		public static int WriteHash(Span<char> destination, Stream stream)
		{
			stream.Seek(0L, SeekOrigin.Begin);
			using MD5 md5 = MD5.Create();
			byte[] hash = md5.ComputeHash(stream);
			
			for (int i = 0; i < hash.Length; i++)
			{
				hash[i].TryFormat(destination.Slice(i * 2), out _, "X2", CultureInfo.InvariantCulture);
			}
			return hash.Length * 2;
		}
	}
}