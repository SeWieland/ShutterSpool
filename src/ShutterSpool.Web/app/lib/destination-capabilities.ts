export function isDestinationKindEnabled(enabledKinds: string[] | undefined, kind: string | null | undefined): boolean {
    return typeof kind === 'string' && enabledKinds?.includes(kind) === true;
}
