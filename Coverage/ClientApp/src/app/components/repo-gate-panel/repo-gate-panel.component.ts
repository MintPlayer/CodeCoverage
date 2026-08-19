import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { BrowseService, GateSettings } from '../../services/browse.service';

/**
 * "Coverage gate" card for the Repository detail page — the policy the
 * check-runs judge against. Manager-only: the panel self-fetches RepoInfo and
 * renders nothing without canManage (the API refuses regardless). Blocking is
 * deliberately presented as the opt-in it is: everything starts informational.
 */
@Component({
  selector: 'app-repo-gate-panel',
  imports: [FormsModule, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent],
  template: `
    @if (canManage() && gate(); as g) {
      <bs-card class="mt-3 d-block">
        <bs-card-header><i class="bi bi-shield-check"></i> Coverage gate</bs-card-header>
        <bs-card-body>
          <div class="row g-3">
            <div class="col-sm-4">
              <label class="form-label small mb-1">Project comparison</label>
              <select class="form-select form-select-sm" [(ngModel)]="g.projectMode">
                <option value="auto">Ratchet against the base commit</option>
                <option value="fixed">Fixed target</option>
              </select>
            </div>
            @if (g.projectMode === 'fixed') {
              <div class="col-sm-4">
                <label class="form-label small mb-1">Project target (%)</label>
                <input type="number" class="form-control form-control-sm" min="0" max="100" step="0.1" [(ngModel)]="g.projectTarget">
              </div>
            }
            <div class="col-sm-4">
              <label class="form-label small mb-1">Allowed drop (points)</label>
              <input type="number" class="form-control form-control-sm" min="0" max="100" step="0.1" [(ngModel)]="g.projectThreshold">
            </div>
            <div class="col-sm-4">
              <label class="form-label small mb-1">Partial builds judge</label>
              <select class="form-select form-select-sm" [(ngModel)]="g.projectBasis">
                <option value="scoped">Scoped baseline (like-for-like)</option>
                <option value="projection">Patched projection (whole workspace)</option>
              </select>
            </div>
            <div class="col-sm-4">
              <label class="form-label small mb-1">Patch target (%)</label>
              <input type="number" class="form-control form-control-sm" min="0" max="100" step="0.1"
                     placeholder="off" [(ngModel)]="g.patchTarget">
            </div>
            <div class="col-sm-4">
              <label class="form-label small mb-1">Patch tolerance (points)</label>
              <input type="number" class="form-control form-control-sm" min="0" max="100" step="0.1" [(ngModel)]="g.patchThreshold">
            </div>
          </div>

          <div class="form-check form-switch mt-3">
            <input class="form-check-input" type="checkbox" id="gateBlocking" [(ngModel)]="g.blocking">
            <label class="form-check-label small" for="gateBlocking">
              Blocking — failed checks turn red. Off, the checks post the same numbers but never fail.
            </label>
          </div>

          <div class="d-flex align-items-center gap-2 mt-3">
            <button class="btn btn-sm btn-primary" (click)="save()" [disabled]="saving()">
              <i class="bi bi-save"></i> Save gate
            </button>
            @if (savedAt()) { <span class="small text-success">Saved.</span> }
            @if (error()) { <span class="small text-danger">{{ error() }}</span> }
          </div>
          <div class="small text-muted mt-2">
            A <code>coverage.yml</code> in the repository overrides these per field, read from the base branch.
          </div>
        </bs-card-body>
      </bs-card>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoGatePanelComponent {
  private readonly browse = inject(BrowseService);

  owner = input.required<string>();
  name = input.required<string>();

  readonly canManage = signal(false);
  readonly gate = signal<GateSettings | null>(null);
  readonly saving = signal(false);
  readonly savedAt = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(async () => {
      const owner = this.owner();
      const name = this.name();
      try {
        const repo = await this.browse.getRepo(owner, name);
        this.canManage.set(repo.canManage);
        if (repo.canManage) {
          this.gate.set(await this.browse.getGate(owner, name));
        }
      } catch {
        this.canManage.set(false);
      }
    });
  }

  async save(): Promise<void> {
    const gate = this.gate();
    if (!gate) return;
    this.saving.set(true);
    this.savedAt.set(false);
    this.error.set(null);
    try {
      this.gate.set(await this.browse.putGate(this.owner(), this.name(), {
        ...gate,
        // An emptied number input round-trips as NaN/'' — the API wants null.
        projectTarget: numberOrNull(gate.projectTarget),
        patchTarget: numberOrNull(gate.patchTarget),
      }));
      this.savedAt.set(true);
    } catch (err) {
      this.error.set((err as { error?: { error?: string } })?.error?.error ?? 'Saving failed.');
    } finally {
      this.saving.set(false);
    }
  }
}

function numberOrNull(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}
