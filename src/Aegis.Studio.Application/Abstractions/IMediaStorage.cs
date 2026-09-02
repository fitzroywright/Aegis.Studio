namespace Aegis.Studio.Application.Abstractions;
public interface IMediaStorage{Task<string> SaveAsync(string fileName,Stream content,CancellationToken cancellationToken);Task<Stream> OpenReadAsync(string storageKey,CancellationToken cancellationToken);}
