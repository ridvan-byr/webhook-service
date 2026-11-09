/** The title of a page. Every page has exactly one, as a level 1 heading. */
export function PageHeading({ title, description }: { title: string; description?: string }) {
  return (
    <header className="mb-4">
      <h1 className="text-lg font-medium">{title}</h1>
      {description ? <p className="mt-1 text-muted">{description}</p> : null}
    </header>
  );
}
