using BuildMind.Domain.Entities;
namespace BuildMind.Application.Abstractions;
public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(Project project, CancellationToken cancellationToken);
}
