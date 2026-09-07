namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Creates the per-operation persistence scope (AD-5). Application services depend on this
/// rather than on a context, which is what keeps the database context out of every constructor
/// in the system and out of the Blazor circuit's lifetime entirely. Application never names an
/// Infrastructure type, this sentence included.
/// </summary>
public interface IUnitOfWorkFactory
{
    /// <summary>Opens a context and begins a transaction. The caller disposes the result.</summary>
    Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken);
}
