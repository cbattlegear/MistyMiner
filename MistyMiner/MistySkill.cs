using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MistyRobotics.Common;
using MistyRobotics.SDK.Commands;
using MistyRobotics.Common.Data;
using MistyRobotics.SDK.Events;
using MistyRobotics.SDK.Responses;
using MistyRobotics.Common.Types;
using MistyRobotics.SDK;
using MistyRobotics.SDK.Messengers;

using Microsoft.AI.MachineLearning;
using Windows.Storage.Streams;
using Windows.Media;
using Windows.Storage;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using System.Threading;
using System.Net.Http;

namespace MistyMiner
{
	internal class YawTracking
    {
		private double _currentYaw = 0;
		private double _targetYaw = 0;

		private double _allowedError = 3;

		public event EventHandler YawReached;

		public double getYaw()
        {
			return _currentYaw;
        }

		public void setYaw(double yaw)
        {
			_currentYaw = yaw;
			CheckYaw();
        }

		public void setTargetYaw(double yaw)
        {
			_targetYaw = yaw;
        }

		private void CheckYaw()
        {
			if(_currentYaw + 180 >= _targetYaw + 180 - _allowedError && _currentYaw + 180 <= _targetYaw + 180 + _allowedError)
            {
				OnYawReached(EventArgs.Empty);
			}
        }

		protected virtual void OnYawReached(EventArgs e)
		{
			EventHandler handler = YawReached;
			if (handler != null)
			{
				handler(this, e);
			}
		}
	}

	internal class ArmTracking
	{
		private double _currentPos = 0;
		private double _targetPos = 0;

		public event EventHandler ArmPosReached;

		public double getArmPos()
		{
			return _currentPos;
		}

		public void setArmPos(double pos)
		{
			_currentPos = pos;
			CheckArmPos();
		}

		public void setTargetArmPos(double pos)
		{
			_targetPos= pos;
		}

		private void CheckArmPos()
		{
			if (_currentPos >= _targetPos - 10 && _currentPos <= _targetPos + 10)
			{
				OnArmPosReached(EventArgs.Empty);
			}
		}

		protected virtual void OnArmPosReached(EventArgs e)
		{
			EventHandler handler = ArmPosReached;
			if (handler != null)
			{
				handler(this, e);
			}
		}
	}
	internal class TimeOfFlightTracking
    {
		private double RightFrontDistance;
		private double CenterFrontDistance;
		private double LeftFrontDistance;
		private double RearCenterDistance;

		private int RightFrontStatus;
		private int CenterFrontStatus;
		private int LeftFrontStatus;
		private int RearCenterStatus;

		private double ThresholdDistance = 0.05;
		private double FarEnoughThresholdDistance = 0;
		private bool FromBack = true;


		public event EventHandler DistanceReached;
		public event EventHandler FarEnough;

		public void SetRightFront(double distance, int status)
        {
			RightFrontDistance = distance;
			RightFrontStatus = status;
			CloseEnough();
			FarEnoughTracking();
        }

		public void SetCenterFront(double distance, int status)
		{
			CenterFrontDistance = distance;
			CenterFrontStatus = status;
			CloseEnough();
			FarEnoughTracking();
		}

		public void SetLeftFront(double distance, int status)
		{
			LeftFrontDistance = distance;
			LeftFrontStatus = status;
			CloseEnough();
			FarEnoughTracking();
		}

		public void SetRearCenter(double distance, int status)
        {
			RearCenterDistance = distance;
			RearCenterStatus = status;
			FarEnoughTracking();
		}

		public double GetRearCenterDistance()
        {
			return RearCenterDistance;
        }

		public double GetMinimumDistance()
        {
			if (LeftFrontDistance < CenterFrontDistance && LeftFrontDistance < RightFrontDistance)
			{
				return LeftFrontDistance;
			} else if (RightFrontDistance < CenterFrontDistance && RightFrontDistance < LeftFrontDistance)
			{
				return RightFrontDistance;
			} else
			{
				return CenterFrontDistance;
			}
		}

		public void SetThresholdDistance(double threshold)
        {
			ThresholdDistance = threshold;
        }

		public void SetFarEnoughThresholdDistance(double threshold, bool fromBack)
		{
			FarEnoughThresholdDistance = threshold;
			FromBack = fromBack;
		}

		public void CloseEnough()
        {
			if((RightFrontDistance < ThresholdDistance && RightFrontStatus < 200 && RightFrontStatus >= 0) ||
				(CenterFrontDistance < ThresholdDistance && CenterFrontStatus < 200 && CenterFrontStatus >= 0) ||
				(LeftFrontDistance < ThresholdDistance && LeftFrontStatus < 200 && LeftFrontStatus >= 0))
            {
				OnDistanceReached(EventArgs.Empty);
            }
            
        }

		public void FarEnoughTracking()
		{
			if(FromBack)
            {
				if(RearCenterDistance > FarEnoughThresholdDistance && RearCenterStatus < 200 && RearCenterStatus >= 0)
                {
					OnFarEnoughReached(EventArgs.Empty);
				}
            } else
            {
				if (GetMinimumDistance() > FarEnoughThresholdDistance)
				{
					OnFarEnoughReached(EventArgs.Empty);
				}
			}
		}

		protected virtual void OnDistanceReached(EventArgs e)
		{
			EventHandler handler = DistanceReached;
			if (handler != null)
			{
				handler(this, e);
			}
		}

		protected virtual void OnFarEnoughReached(EventArgs e)
		{
			EventHandler handler = FarEnough;
			if (handler != null)
			{
				handler(this, e);
			}
		}
	}

	internal class MistySkill : IMistySkill
	{
		private static HttpClient client = new HttpClient();
		private OnnxModel _model = null;
		private const string _ourOnnxFileName = "model.onnx";

		private int celebrationCount = 0;

		private bool alreadyRunning = false;

		private double _currentHeadPitch = 0;

		private TimeOfFlightTracking tof = new TimeOfFlightTracking();
		private YawTracking yaw = new YawTracking();
		private ArmTracking arm = new ArmTracking();

		//private Timer _takeImageTimer;
		/// <summary>
		/// Make a local variable to hold the misty robot interface, call it whatever you want 
		/// </summary>
		private IRobotMessenger _misty;

		/// <summary>
		/// Skill details for the robot
		/// 
		/// There are other parameters you can set if you want:
		///   Description - a description of your skill
		///   TimeoutInSeconds - timeout of skill in seconds
		///   StartupRules - a list of options to indicate if a skill should start immediately upon startup
		///   BroadcastMode - different modes can be set to share different levels of information from the robot using the 'SkillData' websocket
		///   AllowedCleanupTimeInMs - How long to wait after calling OnCancel before denying messages from the skill and performing final cleanup  
		/// </summary>
		public INativeRobotSkill Skill { get; private set; } = new NativeRobotSkill("MistyMiner", "22f78120-2b76-4215-ad7b-47f33d90f65b");

		/// <summary>
		///	This method is called by the wrapper to set your robot interface
		///	You need to save this off in the local variable commented on above as you are going use it to call the robot
		/// </summary>
		/// <param name="robotInterface"></param>
		public void LoadRobotConnection(IRobotMessenger robotInterface)
		{
			_misty = robotInterface;
		}

		/// <summary>
		/// This event handler is called when the robot/user sends a start message
		/// The parameters can be set in the Skill Runner (or as json) and used in the skill if desired
		/// </summary>
		/// <param name="parameters"></param>
		public void OnStart(object sender, IDictionary<string, object> parameters)
		{
			try
			{
				_misty.SendDebugMessage("MistyMiner skill started", null);
				_misty.MoveHead(20, 0, 0, 0, AngularUnit.Position, null);
				//_takeImageTimer = new Timer(CheckForOreCallback, null, 0, 5000);
				_misty.RegisterCapTouchEvent(CheckForOreCallback, 1000, true, null, null, null);
				_misty.RegisterIMUEvent(setYaw, 5, true, null, null, null);

				List<ActuatorPositionValidation> HeadPositionValidation = new List<ActuatorPositionValidation>();
				HeadPositionValidation.Add(new ActuatorPositionValidation { Name = ActuatorPositionFilter.SensorName, Comparison = ComparisonOperator.Equal, ComparisonValue = ActuatorPosition.HeadPitch });
				_misty.RegisterActuatorEvent(setHeadPitch, 10, true, HeadPositionValidation, null, null);

				List<ActuatorPositionValidation> ArmPositionValidation = new List<ActuatorPositionValidation>();
				ArmPositionValidation.Add(new ActuatorPositionValidation { Name = ActuatorPositionFilter.SensorName, Comparison = ComparisonOperator.Equal, ComparisonValue = ActuatorPosition.RightArm });
				_misty.RegisterActuatorEvent(setArmPitch, 100, true, ArmPositionValidation, null, null);

				_misty.RegisterTimeOfFlightEvent(setTimeOfFlight, 0, true, null, null, null);


				//Clean up any lingering runs
				yaw.YawReached -= HandleYawReached;
				yaw.YawReached -= HandleReturnYawReached;
				yaw.YawReached -= FinalYaw;

				tof.DistanceReached -= HandleOreReached;
				tof.DistanceReached -= HandleReturnReached;

				arm.ArmPosReached -= ArmDown;
				arm.ArmPosReached -= ArmUp;

			}
			catch (Exception ex)
			{
				_misty.SkillLogger.Log($"MistyMiner : OnStart: => Exception", ex);
				_misty.SendDebugMessage($"MistyMiner : OnStart: => Exception" +  ex, null); 
			}
		}

		/// <summary>
		/// This event handler is called when Pause is called on the skill
		/// User can save the skill status/data to be retrieved when Resume is called
		/// Infrastructure to help support this still under development, but users can implement this themselves as needed for now 
		/// </summary>
		/// <param name="parameters"></param>
		public void OnPause(object sender, IDictionary<string, object> parameters)
		{
			//In this template, Pause is not implemented by default
		}

		/// <summary>
		/// This event handler is called when Resume is called on the skill
		/// User can restore any skill status/data and continue from Paused location
		/// Infrastructure to help support this still under development, but users can implement this themselves as needed for now 
		/// </summary>
		/// <param name="parameters"></param>
		public void OnResume(object sender, IDictionary<string, object> parameters)
		{
			//TODO Put your code here and update the summary above
		}
		
		/// <summary>
		/// This event handler is called when the cancel command is issued from the robot/user
		/// You currently have a few seconds to do cleanup and robot resets before the skill is shut down... 
		/// Events will be unregistered for you 
		/// </summary>
		public void OnCancel(object sender, IDictionary<string, object> parameters)
		{
			yaw.YawReached -= HandleYawReached;
			yaw.YawReached -= HandleReturnYawReached;
			yaw.YawReached -= FinalYaw;

			tof.DistanceReached -= HandleOreReached;
			tof.DistanceReached -= HandleReturnReached;

			arm.ArmPosReached -= ArmDown;
			arm.ArmPosReached -= ArmUp;

			yaw = null;
			tof = null;
			arm = null;

			celebrationCount = 0;

			_model = null;
			//TODO Put your code here and update the summary above
		}

		/// <summary>
		/// This event handler is called when the skill timeouts
		/// You currently have a few seconds to do cleanup and robot resets before the skill is shut down... 
		/// Events will be unregistered for you 
		/// </summary>
		public void OnTimeout(object sender, IDictionary<string, object> parameters)
		{
			//TODO Put your code here and update the summary above
		}

		public void OnResponse(IRobotCommandResponse response)
		{
			Debug.WriteLine("Response: " + response.ResponseType.ToString());
			_misty.SendDebugMessage("Response: " + response.ResponseType.ToString(), null);
		}

		private void setYaw(IIMUEvent info)
        {
			if (info.Yaw >= 0)
			{
				if (info.Yaw > 180)
					yaw.setYaw(info.Yaw - 360);
				else
					yaw.setYaw(info.Yaw);
			}
		}

		private void setHeadPitch(IActuatorEvent info)
        {
			_currentHeadPitch = info.ActuatorValue;
		}

		private void setArmPitch(IActuatorEvent info)
		{
			arm.setArmPos(info.ActuatorValue);
		}

		private void setTimeOfFlight(ITimeOfFlightEvent info)
		{
			switch (info.SensorPosition)
			{
				case TimeOfFlightPosition.FrontCenter:
					tof.SetCenterFront(info.DistanceInMeters, info.Status);
					break;
				case TimeOfFlightPosition.FrontLeft:
					tof.SetLeftFront(info.DistanceInMeters, info.Status);
					break;
				case TimeOfFlightPosition.FrontRight:
					tof.SetRightFront(info.DistanceInMeters, info.Status);
					break;
				case TimeOfFlightPosition.Back:
					tof.SetRearCenter(info.DistanceInMeters, info.Status);
					break;
			}
		}

		private async void CheckForOreCallback(object info)
        {

			if(alreadyRunning)
            {
				await _misty.SendDebugMessageAsync("Already running, please wait.");
				return;
            }

			alreadyRunning = true;

			await _misty.ChangeLEDAsync(0, 255, 0);
			celebrationCount = 0;
			await LoadModelAsync();

			if (tof.GetRearCenterDistance() < 0.25)
            {
				tof.SetFarEnoughThresholdDistance(0.25, true);
				tof.FarEnough += InitialDistanceCheck;
				await _misty.DriveAsync(15, 0);
			} else
            {
				RunCustomVision();

			}
		}

		async void InitialDistanceCheck(object sender, EventArgs e)
        {
			tof.FarEnough -= InitialDistanceCheck;
			await _misty.StopAsync();
			RunCustomVision();
		}

		async void RunCustomVision()
        {
			try
			{
				_misty.SkillLogger.Log("Taking picture to analyze");
				_misty.SendDebugMessage("Taking picture to analyze", null);
				ITakePictureResponse takePictureResponse = await _misty.TakePictureAsync("oretest.jpg", false, true, true, 640, 480);

				_misty.SendDebugMessage("Picture taken", null);
				SoftwareBitmap softwareBitmap;
				using (IRandomAccessStream stream = new MemoryStream((byte[])takePictureResponse.Data.Image).AsRandomAccessStream())
				{
					stream.Seek(0);
					// Create the decoder from the stream 
					BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

					// Get the SoftwareBitmap representation of the file in BGRA8 format
					softwareBitmap = await decoder.GetSoftwareBitmapAsync();
					softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
				}

				// Encapsulate the image in the WinML image type (VideoFrame) to be bound and evaluated
				VideoFrame inputImage = VideoFrame.CreateWithSoftwareBitmap(softwareBitmap);
				_misty.SendDebugMessage("Picture processed, sending to model", null);

				// Evaluate the image
				OnnxModelOutput output = await EvaluateVideoFrameAsync(inputImage);

				_misty.SendDebugMessage("Model finished eval", null);

				await _misty.DisplayImageAsync("e_DefaultContent.jpg", 100);

				if (output == null)
				{
					_misty.SendDebugMessage("Model output empty", null);
					_misty.ChangeLED(0, 0, 0, OnResponse);
					alreadyRunning = false;
				}
				else
				{
					int vectorCount = output.detected_classes.GetAsVectorView().Count;
					double initialScore = output.detected_scores.GetAsVectorView()[0];
					long initialClass = output.detected_classes.GetAsVectorView()[0];

					if (vectorCount == 0 || initialScore < 0.25)
					{
						_misty.ChangeLED(0, 0, 0, OnResponse);
						alreadyRunning = false;

					}
					else if (initialClass == 1 && initialScore >= 0.25)
					{
						_misty.ChangeLED(255, 0, 0, OnResponse);
						_misty.RunSkill("e1fcbf5b-9163-4d09-8707-bffd00ddcd5d", null, null);
						alreadyRunning = false;

					}
					else if (initialClass == 0 && initialScore >= 0.25)
					{
						_misty.ChangeLED(0, 0, 255, OnResponse);
						//Say found Ore
						//_misty.RunSkill("a61832ab-6bc1-4f1a-9de1-0d1dc8bf3ff0", null, null);

						var data = new StringContent("{ \"text\":\"Ore Found!\",\"pitch\":0,\"speechRate\":0,\"voice\":null,\"flush\":false,\"utteranceId\":null }", Encoding.UTF8, "application/json");
						HttpResponseMessage result = await client.PostAsync("http://127.0.0.1/api/tts/speak?text=Ore Found!&pitch=0&speechRate=0&flush=false", data);

						double calcTrajectory = yaw.getYaw() + (25 * (((output.detected_boxes.GetAsVectorView()[0] + output.detected_boxes.GetAsVectorView()[2]) / 2) - 0.5) * -1);

						await _misty.SendDebugMessageAsync("Trajectory: " + calcTrajectory);
						//Take the current yaw of the robot and then add the box X axis percentage
						//The 20 number is approximately how many degrees you have to rotate to go from edge to center of the camera

						if (calcTrajectory > yaw.getYaw())
						{
							await _misty.DriveAsync(0, 5);
						}
						else
						{
							await _misty.DriveAsync(0, -5);
						}


						//data = new StringContent("{ \"heading\":" + calcTrajectory.ToString() + ",\"radius\":0,\"timeMs\":3000,\"reverse\":false }", Encoding.UTF8, "application/json");
						//result = await client.PostAsync("http://127.0.0.1/api/drive/arc", data);

						yaw.setTargetYaw(calcTrajectory);

						yaw.YawReached += HandleYawReached;

						calcTrajectory = _currentHeadPitch + (80 * (((output.detected_boxes.GetAsVectorView()[1] + output.detected_boxes.GetAsVectorView()[3]) / 2) - 0.5));

						await _misty.MoveHeadAsync(calcTrajectory, 0, 0, 100, AngularUnit.Degrees);

						//_misty.DriveArc(calcTrajectory, 0.2, 2000, false, null);


						//357.47 deg 50% at 2sec = 341.88 16 degree 342.46 

					}
				}
			}
			catch (Exception ex)
			{
				_misty.SendDebugMessage($"error: {ex.Message}", null);
				_misty.SendDebugMessage("Picture processing failed", null);
			}
		}

		async void HandleYawReached(object sender, EventArgs e)
        {
			yaw.YawReached -= HandleYawReached;
			_misty.SendDebugMessage("Hit initial rotation" + yaw.getYaw(), null);
			
			tof.SetThresholdDistance(0.2);
			tof.DistanceReached += HandleOreReached;
			Thread.Sleep(1000);
			await _misty.DriveAsync(20, 0);
		}

		async void HandleOreReached(object sender, EventArgs e)
		{
			tof.DistanceReached -= HandleOreReached;
			await _misty.StopAsync();
			_misty.SendDebugMessage("Ore reached", null);
			MineAndCelebrate();
		}

		async void MineAndCelebrate()
		{
			_misty.SendDebugMessage("Celebration beginning", null);

			arm.setTargetArmPos(50);
			arm.ArmPosReached += ArmDown;
			await _misty.MoveArmsAsync(0, 50, null, null, null, AngularUnit.Position);
		}

		async void ArmDown(object sender, EventArgs e)
		{
			arm.ArmPosReached -= ArmDown;
			_misty.SendDebugMessage("Celebration number " + celebrationCount, null);
			celebrationCount++;
			if (celebrationCount > 3)
			{
				celebrationCount = 0;

				var data = new StringContent("{ \"text\":\"Diamonds!\",\"pitch\":0,\"speechRate\":0,\"voice\":null,\"flush\":false,\"utteranceId\":null }", Encoding.UTF8, "application/json");
				HttpResponseMessage result = await client.PostAsync("http://127.0.0.1/api/tts/speak?text=Ore Found!&pitch=0&speechRate=0&flush=false", data);

				if (tof.GetMinimumDistance() < 0.3)
				{
					tof.SetFarEnoughThresholdDistance(0.3, false);
					tof.FarEnough += BackupFromOreBlock;
					await _misty.MoveArmsAsync(0, -25, null, null, null, AngularUnit.Position);
					await _misty.DriveAsync(-15, 0);
				} else
                {
					DriveBack();
				}
			}
			else
			{
				await _misty.PlayAudioAsync("MineCraftHit.mp3", 100);
				arm.setTargetArmPos(-25);
				arm.ArmPosReached += ArmUp;
				await _misty.MoveArmsAsync(0, -25, null, null, null, AngularUnit.Position);
			}
		}

		async void ArmUp(object sender, EventArgs e)
		{
			arm.ArmPosReached -= ArmUp;
			_misty.SendDebugMessage("Arm going back up", null);
			arm.setTargetArmPos(50);
			arm.ArmPosReached += ArmDown;
			await _misty.MoveArmsAsync(0, 50, null, null, null, AngularUnit.Position);
		}

		async void BackupFromOreBlock(object sender, EventArgs e)
        {
			tof.FarEnough -= BackupFromOreBlock;
			await _misty.StopAsync();
			DriveBack();
		}

		async void DriveBack()
		{
			_misty.SendDebugMessage("Starting drive back process", null);
			double targetYaw = yaw.getYaw() + 180 > 180 ? yaw.getYaw() + 180 - 360 : yaw.getYaw() + 180;
			_misty.SendDebugMessage("Target Yaw " + targetYaw, null);
			yaw.setTargetYaw(targetYaw);
			yaw.YawReached += HandleReturnYawReached;
			//StringContent data = new StringContent("{ \"heading\":" + targetYaw.ToString() + ",\"radius\":0,\"timeMs\":3000,\"reverse\":false }", Encoding.UTF8, "application/json");
			//HttpResponseMessage result = await client.PostAsync("http://127.0.0.1/api/drive/arc", data);
			Thread.Sleep(1000);
			await _misty.DriveAsync(0, 20);
		}

		async void HandleReturnYawReached(object sender, EventArgs e)
		{
			yaw.YawReached -= HandleReturnYawReached;
			await _misty.StopAsync();
			_misty.SendDebugMessage("Hit final rotation" + yaw.getYaw(), null);
			
			tof.SetThresholdDistance(0.4);
			tof.DistanceReached += HandleReturnReached;

			Thread.Sleep(1000);
			await _misty.DriveAsync(20, 0);
		}

		async void HandleReturnReached(object sender, EventArgs e)
		{
			tof.DistanceReached -= HandleReturnReached;
			await _misty.StopAsync();
			_misty.SendDebugMessage("Returned to start", null);

			if (tof.GetMinimumDistance() < 0.4)
			{
				tof.SetFarEnoughThresholdDistance(0.4, false);
				tof.FarEnough += BackupFromWall;
				Thread.Sleep(1000);
				await _misty.DriveAsync(-15, 0);
			} else
            {
				StartFinalRotation();
            }
		}

		async void BackupFromWall(object sender, EventArgs e)
        {
			tof.FarEnough -= BackupFromWall;
			await _misty.StopAsync();
			StartFinalRotation();
		}

		async void StartFinalRotation()
        {
			yaw.setTargetYaw(0);
			yaw.YawReached += FinalYaw;

			Thread.Sleep(1000);
			await _misty.DriveAsync(0, 20);
		}
		async void FinalYaw(object sender, EventArgs e)
		{
			yaw.YawReached -= FinalYaw;

			yaw.YawReached -= HandleYawReached;
			yaw.YawReached -= HandleReturnYawReached;
			yaw.YawReached -= FinalYaw;

			tof.DistanceReached -= HandleOreReached;
			tof.DistanceReached -= HandleReturnReached;

			arm.ArmPosReached -= ArmDown;
			arm.ArmPosReached -= ArmUp;

			alreadyRunning = false;

			await _misty.StopAsync();
			_misty.SendDebugMessage("Returned to initial yaw" + yaw.getYaw(), null);
			await _misty.ChangeLEDAsync(0, 0, 0);
			_misty.MoveHead(20, 0, 0, 0, AngularUnit.Position, null);
			_misty.SendDebugMessage("Finished.", null);
		}

		

		

		#region IDisposable Support
		private bool _isDisposed = false;

		private void Dispose(bool disposing)
		{
			if (!_isDisposed)
			{
				if (disposing)
				{
					// TODO: dispose managed state (managed objects).
				}

				// TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
				// TODO: set large fields to null.

				_isDisposed = true;
			}
		}

		// TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
		// ~MistySkill() {
		//   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		//   Dispose(false);
		// }

		// This code added to correctly implement the disposable pattern.
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
			// TODO: uncomment the following line if the finalizer is overridden above.
			// GC.SuppressFinalize(this);
		}
		#endregion

		private async Task LoadModelAsync()
		{
			try
			{

				var modelFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri($"ms-appx:///Model/{_ourOnnxFileName}"));
				_model = await OnnxModel.CreateFromStreamAsync(modelFile);

				_misty.SkillLogger.Log($"Loaded {_ourOnnxFileName}");
				_misty.SendDebugMessage($"Loaded {_ourOnnxFileName}", null);
			}
			catch (Exception ex)
			{
				_misty.SendDebugMessage($"error: {ex.Message}", null);
				_model = null;
			}
		}

		private async Task<OnnxModelOutput> EvaluateVideoFrameAsync(VideoFrame frame)
		{
			if (frame != null)
			{
				try
				{
					OnnxModelInput inputData = new OnnxModelInput();
					inputData.data = frame;
					var results = await _model.EvaluateAsync(inputData);

					_misty.SendDebugMessage("Boxes: " + string.Join(", ", results.detected_boxes.GetAsVectorView()) + " Classes: " + string.Join(", ", results.detected_classes.GetAsVectorView()) + " Scores: " + string.Join(", ", results.detected_scores.GetAsVectorView()), null);

					return results;
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"error: {ex.Message}");
					_misty.SendDebugMessage($"error: {ex.Message}", null);

					return null;
				}
			}

			return null;
		}
	}

	public sealed class OnnxModel
	{
		private LearningModel learningModel;
		private LearningModelSession session;
		private LearningModelBinding binding;

		public static IAsyncOperation<OnnxModel> CreateFromStreamAsync(IRandomAccessStreamReference stream)
        {
			return CreateFromStreamAsyncHelper(stream).AsAsyncOperation();

		}

		public IAsyncOperation<OnnxModelOutput> EvaluateAsync(OnnxModelInput input)
		{
			return this.EvaluateAsyncHelper(input).AsAsyncOperation();

		}

		private static async Task<OnnxModel> CreateFromStreamAsyncHelper(IRandomAccessStreamReference stream)
		{
			OnnxModel onnxModel = new OnnxModel();
			onnxModel.learningModel = await LearningModel.LoadFromStreamAsync(stream);
			onnxModel.session = new LearningModelSession(onnxModel.learningModel);
			onnxModel.binding = new LearningModelBinding(onnxModel.session);
			return onnxModel;
		}

		private async Task<OnnxModelOutput> EvaluateAsyncHelper(OnnxModelInput input)
		{
			binding.Bind("image_tensor", input.data);
			var result = await session.EvaluateAsync(binding, string.Empty);

			OnnxModelOutput output = new OnnxModelOutput();
			output.detected_boxes = result.Outputs["detected_boxes"] as TensorFloat;
			output.detected_classes = result.Outputs["detected_classes"] as TensorInt64Bit;
			output.detected_scores = result.Outputs["detected_scores"] as TensorFloat;

			return output;
		}


	}

	public sealed class OnnxModelInput
	{
		public VideoFrame data { get; set; }
	}

	public sealed class OnnxModelOutput
	{
		internal TensorFloat detected_boxes;  // shape(1,-1,4)
		internal TensorInt64Bit detected_classes { get; set; } // shape(1,-1)
		internal TensorFloat detected_scores { get; set; } // shape(1,-1)
	}
}
