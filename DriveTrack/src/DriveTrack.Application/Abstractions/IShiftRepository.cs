using DriveTrack.Domain.Shifts;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="Shift"/>. No <c>Remove</c>: nothing in the requirements deletes a
/// shift, and NFR-6 keeps a member that nothing asks for out of the surface.
/// </summary>
public interface IShiftRepository
{
    /// <summary>Loads a shift, or null when there is none with that id.</summary>
    Task<Shift?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Stages a new shift for the next commit.</summary>
    void Add(Shift shift);
}
