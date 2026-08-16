# Setup Agent TODO

- Wire up the "Cancel" button on `ConnectionEstablishedForm` to actually abort the Windows
  Update flow. Currently disabled/non-functional - the update loop (`Program.RunUpdateLoop`)
  has no cancellation path to hook it into yet (e.g. a `CancellationToken` threaded through
  `WindowsUpdateSearcher.Search`/`IWindowsUpdateInstaller.DownloadAndInstall`, checked between
  phases - mid-download/install cancellation would need WUA's own cancel API, not just
  abandoning the call).

- Block windows from asking permission to use Location, answer No.

- Check if more updates have been added to the list during update.
