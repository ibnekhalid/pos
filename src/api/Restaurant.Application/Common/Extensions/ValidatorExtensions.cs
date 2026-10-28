using FluentValidation;
using Restaurant.Application.Common.Exceptions;

namespace Restaurant.Application.Common.Extensions;

public static class ValidatorExtensions
{
    public static async Task ValidateAndThrowAppExceptionAsync<T>(this IValidator<T> validator, T instance, CancellationToken cancellationToken = default)
    {
        var result = await validator.ValidateAsync(instance, cancellationToken);
        if (!result.IsValid)
        {
            throw new ValidationAppException(result.Errors);
        }
    }
}
