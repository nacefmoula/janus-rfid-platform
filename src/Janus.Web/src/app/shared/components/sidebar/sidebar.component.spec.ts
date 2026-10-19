import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SidebarComponent } from './sidebar.component';
import { SessionService } from '../../../core/services/session.service';

describe('SidebarComponent', () => {
  let component: SidebarComponent;
  let fixture: ComponentFixture<SidebarComponent>;
  let session: SessionService;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [SidebarComponent],
      providers: [
        provideRouter([]),
        SessionService
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SidebarComponent);
    component = fixture.componentInstance;
    session = TestBed.inject(SessionService);
    fixture.detectChanges();
  });

  afterEach(() => {
    localStorage.clear();
  });

  it('devrait créer le composant de navigation latérale', () => {
    expect(component).toBeTruthy();
    expect(component.isOpen).toBeFalse();
  });

  it('devrait émettre closeSidebar lors d\'un clic sur un lien de navigation', () => {
    spyOn(component.closeSidebar, 'emit');

    component.onNavClick();

    expect(component.closeSidebar.emit).toHaveBeenCalled();
  });

  it('devrait appliquer la classe open lorsque isOpen est vrai', () => {
    component.isOpen = true;
    fixture.detectChanges();

    const aside: HTMLElement | null = fixture.nativeElement.querySelector('aside.sidebar');
    expect(aside).toBeTruthy();
    expect(aside?.classList.contains('open')).toBeTrue();
  });
});
