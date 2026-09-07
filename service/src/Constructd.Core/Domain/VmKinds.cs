namespace Constructd.Core.Domain;

public enum VmKind { Primary, Child }
public enum SharingScope { Private, Host, Selected }
public enum VmTokenKind { Legacy, Primary }
public enum LeaseState { Inactive, Active, Unlimited, Expired, Overdue }
public enum CapabilityLevel { Unsupported, Conditional, Supported }
public enum SecureBootTemplate { MicrosoftWindows, MicrosoftUefiCertificateAuthority }
public enum BootDevice { InstallMedia, AuxiliaryMedia, Disk, Network }
public enum GuestReportProvenance { Unknown, Provisioner }
public enum GuestAddressFamily { Ipv4, Ipv6 }
public enum GuestAddressSource { Kvp, Dhcp, Unknown }
public enum MediaRole { Install, Auxiliary }
public enum MediaSource { Url, Upload }
public enum MediaState { Pending, Transferring, Ready, Failed, Deleting }
public enum MediaSlot { Install, Auxiliary }
public enum UploadState { Open, Completing, Done, Aborted, Expired }
public enum ReservationResource { Ram, Cpu, Storage }
public enum ReservationPhase { Pending, Held }
public enum ReservationOrigin { Api, External, Reconcile }
public enum CapacityMode { Observe, Enforce }
public enum GracefulShutdownOutcome { Completed, Timeout, Unavailable, Failed }
public enum MaintenanceState { Open, Draining, Maintenance }
public enum CascadeState { Previewed, Accepted, Running, Failed, Completed, Expired }
public enum HostUpdateState
{
    Checking, Staged, StageFailed, Draining, HandedOff, Applying, Succeeded, ApplyFailed,
    RolledBack, RolledBackWithDatabase, RecoveryFailed, Interrupted, Cancelled, ResolvedByAdmin,
}
public enum ForwardRelationship { Self, Admin, Owner, Parent, Shared }
public enum ChildAction
{
    Inspect, Start, Shutdown, Save, Restart, Delete, Share, Renew, Hardware, Media,
    Console, ForwardClient, ForwardHost, Addresses, Overrides, RotateToken,
}
