namespace HdmiCaptureCardMonitor.Capture.Abstractions;

// Phase 0 contracts only. Concrete capture backends are intentionally deferred.
public interface ICaptureDeviceEnumerator { }
public interface ICaptureBackend { }
public interface IVideoPreviewRenderer { }
public interface IAudioMonitor { }
public interface IRecordingService { }
public interface IDeviceRecoveryService { }
public interface ICaptureDiagnostics { }
