using Microsoft.Extensions.Options;

namespace BuildMind.Infrastructure.Tests.Support;

/// <summary>Finds a specific exception inside wrapped/aggregate host start-up failures.</summary>
public static class ExceptionChains
{
    public static OptionsValidationException RequireOptionsValidationException(Exception? exception)
    {
        Assert.NotNull(exception);
        var pending = new Queue<Exception>([exception!]);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (current is OptionsValidationException validation)
            {
                return validation;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected an OptionsValidationException but got {exception!.GetType().FullName}.");
    }
}
